﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.PowerShell.Commands;
using Microsoft.PowerShell.Internal;
using Microsoft.PowerShell.PSReadLine;

[module: SuppressMessage("Microsoft.Design", "CA1014:MarkAssembliesWithClsCompliant")]
[module: SuppressMessage("Microsoft.Design", "CA1026:DefaultParametersShouldNotBeUsed")]

namespace Microsoft.PowerShell
{
    class ExitException : Exception { }
    class LineAcceptedException : Exception { }

    public partial class PSConsoleReadLine : IPSConsoleReadLineMockableMethods
    {
        private const int ConsoleExiting = 1;

        // *must* be initialized in the static ctor
        // because the static member _clipboard depends upon it
        // for its own initialization
        private static readonly PSConsoleReadLine _singleton;

        // This is used by PowerShellEditorServices (the backend of the PowerShell VSCode extension)
        // so that it can call PSReadLine from a delegate and not hit nested pipeline issues.
        #pragma warning disable CS0649
        private static Action<CancellationToken> _handleIdleOverride;
        #pragma warning restore CS0649

        private bool _delayedOneTimeInitCompleted;
        // This is used by AIShell to check if PSReadLine is initialized and ready to render.
        #pragma warning disable CS0414
        private bool _readLineReady;
        #pragma warning restore CS0414
        private bool _lineAcceptedExceptionThrown;

        private IPSConsoleReadLineMockableMethods _mockableMethods;
        private IConsole _console;
        private ICharMap _charMap;
        private Encoding _initialOutputEncoding;
        private bool _skipOutputEncodingChange;
        private EngineIntrinsics _engineIntrinsics;
        private Thread _readKeyThread;
        private AutoResetEvent _readKeyWaitHandle;
        private AutoResetEvent _keyReadWaitHandle;
        private CancellationToken _cancelReadCancellationToken;
        internal ManualResetEvent _closingWaitHandle;
        private WaitHandle[] _threadProcWaitHandles;
        private WaitHandle[] _requestKeyWaitHandles;

        private readonly StringBuilder _buffer;
        private readonly StringBuilder _statusBuffer;
        private bool _statusIsErrorMessage;
        private string _statusLinePrompt;
        private string _acceptedCommandLine;
        private List<EditItem> _edits;
        private int _editGroupStart;
        private int _undoEditIndex;
        private int _mark;
        private bool _inputAccepted;
        private readonly Queue<PSKeyInfo> _queuedKeys;
        private Stopwatch _lastRenderTime;
        private static readonly Stopwatch _readkeyStopwatch = new Stopwatch();

        // Save a fixed # of keys so we can reconstruct a repro after a crash
        private static readonly HistoryQueue<PSKeyInfo> _lastNKeys = new HistoryQueue<PSKeyInfo>(200);

        // Tokens etc.
        private Token[] _tokens;
        private Ast _ast;
        private ParseError[] _parseErrors;

        bool IPSConsoleReadLineMockableMethods.RunspaceIsRemote(Runspace runspace)
        {
            return runspace?.ConnectionInfo != null;
        }

        private void ReadOneOrMoreKeys()
        {
            _readkeyStopwatch.Restart();
            while (_console.KeyAvailable)
            {
                // _charMap is only guaranteed to accumulate input while KeyAvailable
                // returns false. Make sure to check KeyAvailable after every ProcessKey call,
                // and clear it in a loop in case the input was something like ^[[1 which can
                // be 3, 2, or part of 1 key depending on timing.
                _charMap.ProcessKey(_console.ReadKey());
                while (_charMap.KeyAvailable)
                {
                    var key = PSKeyInfo.FromConsoleKeyInfo(_charMap.ReadKey());
                    _lastNKeys.Enqueue(key);
                    _queuedKeys.Enqueue(key);
                }
                if (_readkeyStopwatch.ElapsedMilliseconds > 2)
                {
                    // Don't spend too long in this loop if there are lots of queued keys
                    break;
                }
            }

            if (_queuedKeys.Count == 0)
            {
                while (!_charMap.KeyAvailable)
                {
                    // Don't want to block when there is an escape sequence being read.
                    if (_charMap.InEscapeSequence)
                    {
                        if (_console.KeyAvailable)
                        {
                            _charMap.ProcessKey(_console.ReadKey());
                        }
                        else
                        {
                            // We don't want to sleep for the whole escape timeout
                            // or the user will have a laggy console, but there's
                            // nothing to block on at this point either, so do a
                            // small sleep to yield the CPU while we're waiting
                            // to decide what the input was. This will only run
                            // if there are no keys waiting to be read.
                            Thread.Sleep(5);
                        }
                    }
                    else
                    {
                        _charMap.ProcessKey(_console.ReadKey());
                    }
                }
                while (_charMap.KeyAvailable)
                {
                    var key = PSKeyInfo.FromConsoleKeyInfo(_charMap.ReadKey());
                    _lastNKeys.Enqueue(key);
                    _queuedKeys.Enqueue(key);
                }
            }
        }

        private void ReadKeyThreadProc()
        {
            while (true)
            {
                // Wait until ReadKey tells us to read a key (or it's time to exit).
                int handleId = WaitHandle.WaitAny(_threadProcWaitHandles);
                if (handleId == 1) // It was the _closingWaitHandle that was signaled.
                    break;

                ReadOneOrMoreKeys();

                // One or more keys were read - let ReadKey know we're done.
                _keyReadWaitHandle.Set();
            }
        }

        internal static PSKeyInfo ReadKey()
        {
            // Reading a key is handled on a different thread.  During process shutdown,
            // PowerShell will wait in it's ConsoleCtrlHandler until the pipeline has completed.
            // If we're running, we're most likely blocked waiting for user input.
            // This is a problem for two reasons.  First, exiting takes a long time (5 seconds
            // on Win8) because PowerShell is waiting forever, but the OS will forcibly terminate
            // the console.  Also - if there are any event handlers for the engine event
            // PowerShell.Exiting, those handlers won't get a chance to run.
            //
            // By waiting for a key on a different thread, our pipeline execution thread
            // (the thread ReadLine is called from) avoid being blocked in code that can't
            // be unblocked and instead blocks on events we control.
            if (_singleton._lineAcceptedExceptionThrown)
            {
                // If we threw a 'LineAcceptedException', it means that "AcceptLine" was called within an 'OnIdle' handler the last time
                // this method was called, and thus we didn't wait for '_keyReadWaitHandle' to be signalled by the 'readkey thread'.
                // In this case, we don't want to signal '_readKeyWaitHandle' again as the 'readkey thread' already got a chance to run.
                _singleton._lineAcceptedExceptionThrown = false;
            }
            else
            {
                // Set an event so the 'readkey thread' actually attempts to read a key.
                _singleton._readKeyWaitHandle.Set();
            }

            int handleId;
            System.Management.Automation.PowerShell ps = null;

            try
            {
                while (true)
                {
                    // Next, wait for one of three things:
                    //   - a key is pressed
                    //   - the console is exiting
                    //   - 300ms timeout - to process events if we're idle
                    handleId = WaitHandle.WaitAny(_singleton._requestKeyWaitHandles, 300);
                    if (handleId != WaitHandle.WaitTimeout)
                    {
                        break;
                    }

                    if (_handleIdleOverride is not null)
                    {
                        _handleIdleOverride(_singleton._cancelReadCancellationToken);
                        continue;
                    }

                    // If we timed out, check for event subscribers (which is just
                    // a hint that there might be an event waiting to be processed.)
                    var eventSubscribers = _singleton._engineIntrinsics?.Events.Subscribers;
                    int bufferLen = _singleton._buffer.Length;
                    if (eventSubscribers?.Count > 0)
                    {
                        bool runPipelineForEventProcessing = false;
                        foreach (var sub in eventSubscribers)
                        {
                            if (string.Equals(sub.SourceIdentifier, PSEngineEvent.OnIdle, StringComparison.OrdinalIgnoreCase))
                            {
                                // If the buffer is not empty, let's not consider we are idle because the user is in the middle of typing something.
                                if (bufferLen > 0)
                                {
                                    continue;
                                }

                                // There is an 'OnIdle' event subscriber and we are idle because we timed out and the buffer is empty.
                                // Normally PowerShell generates this event, but now PowerShell assumes the engine is not idle because
                                // it called 'PSConsoleHostReadLine' which isn't returning. So we generate the event instead.
                                runPipelineForEventProcessing = true;
                                _singleton._engineIntrinsics.Events.GenerateEvent(
                                    PSEngineEvent.OnIdle,
                                    sender: null,
                                    args: null,
                                    extraData: null);

                                // Break out so we don't genreate more than one 'OnIdle' event for a timeout.
                                break;
                            }

                            runPipelineForEventProcessing = true;
                        }

                        // If there are any event subscribers, run a tiny useless PowerShell pipeline
                        // so that the events can be processed.
                        if (runPipelineForEventProcessing)
                        {
                            if (ps == null)
                            {
                                ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
                                ps.AddScript("[System.Diagnostics.DebuggerHidden()]param() 0", useLocalScope: true);
                            }

                            // To detect output during possible event processing, see if the cursor moved and rerender if so.
                            int cursorTop = _singleton._console.CursorTop;

                            // Start the pipeline to process events.
                            ps.Invoke();

                            // Check if any event handler writes console output to the best of our effort, and adjust the initial coordinates in that case.
                            //
                            // I say "to the best of our effort" because the delegate handler for an event will mostly run on a background thread, and thus
                            // there is no guarantee about when the delegate would finish. So in an extreme case, there could be race conditions in console
                            // read/write: we are reading 'CursorTop' while the delegate is writing console output on a different thread.
                            // There is no much we can do about that extreme case. However, our focus here is the 'OnIdle' event, and its handler is usually
                            // a script block, which will run within the 'ps.Invoke()' call above.
                            //
                            // We detect new console output by checking if cursor top changed, but handle a very special case: an event handler changed our
                            // buffer, by calling 'Insert' for example.
                            // I know only checking on buffer length change doesn't cover the case where buffer changed but the length is the same. However,
                            // we mainly want to cover buffer changes made by an 'OnIdle' event handler, and we trigger 'OnIdle' event only if the buffer is
                            // empty. So, this check is efficient and good enough for that main scenario.
                            // When our buffer was changed by an event handler, we assume that was all the event handler did and there was no direct console
                            // output. So, we adjust the initial coordinates only if cursor top changed but there was no buffer change.
                            int newCursorTop = _singleton._console.CursorTop;
                            int newBufferLen = _singleton._buffer.Length;
                            if (cursorTop != newCursorTop && bufferLen == newBufferLen)
                            {
                                _singleton._initialY = newCursorTop;
                                if (bufferLen > 0)
                                {
                                    _singleton.Render();
                                }
                            }

                            if (_singleton._inputAccepted && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                // 'AcceptLine' was called by an 'OnIdle' handler.
                                // In this case, we only want to break out of the loop and accept the current input on Windows, because
                                // accepting input without a keystroke would leave the 'readkey thread' blocked on the 'ReadKey()' call,
                                // and that will make all subsequent writes to console blocked on Linux and macOS until a key is pressed.
                                _singleton._lineAcceptedExceptionThrown = true;
                                throw new LineAcceptedException();
                            }
                        }
                    }
                }
            }
            finally
            {
                ps?.Dispose();
            }

            if (handleId == ConsoleExiting)
            {
                // The console is exiting - throw an exception to unwind the stack to the point
                // where we can return from ReadLine.
                if (_singleton.Options.HistorySaveStyle == HistorySaveStyle.SaveAtExit)
                {
                    _singleton.SaveHistoryAtExit();
                }
                _singleton._historyFileMutex.Dispose();

                throw new OperationCanceledException();
            }

            if (_singleton._cancelReadCancellationToken.IsCancellationRequested)
            {
                // ReadLine was cancelled. Dequeue the dummy input sent by the host, save the current
                // line to be restored next time ReadLine is called, clear the buffer and throw an
                // exception so we can return an empty string.
                _singleton._queuedKeys.Dequeue();
                _singleton.SaveCurrentLine();
                _singleton._getNextHistoryIndex = _singleton._history.Count;
                _singleton._current = 0;
                _singleton._buffer.Clear();
                _singleton.Render();
                throw new OperationCanceledException();
            }

            var key = _singleton._queuedKeys.Dequeue();
            return key;
        }

        private void PrependQueuedKeys(PSKeyInfo key)
        {
            if (_queuedKeys.Count > 0)
            {
                // This should almost never happen so being inefficient is fine.
                var list = new List<PSKeyInfo>(_queuedKeys);
                _queuedKeys.Clear();
                _queuedKeys.Enqueue(key);
                list.ForEach(k => _queuedKeys.Enqueue(k));
            }
            else
            {
                _queuedKeys.Enqueue(key);
            }
        }

        /// <summary>
        /// Entry point - called from the PowerShell function PSConsoleHostReadLine
        /// after the prompt has been displayed.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine(Runspace runspace, EngineIntrinsics engineIntrinsics, bool? lastRunStatus)
        {
            return ReadLine(runspace, engineIntrinsics, CancellationToken.None, lastRunStatus);
        }

        /// <summary>
        /// Entry point - called by custom PSHost implementations that require the
        /// ability to cancel ReadLine.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine(
            Runspace runspace,
            EngineIntrinsics engineIntrinsics,
            CancellationToken cancellationToken,
            bool? lastRunStatus)
        {
            var console = _singleton._console;

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                // System.Console doesn't handle redirected input. It matches the behavior on Windows
                // by throwing an "InvalidOperationException".
                // Therefore, if either stdin or stdout is redirected, PSReadLine doesn't really work,
                // so throw and let PowerShell call Console.ReadLine or do whatever else it decides to do.
                //
                // Some CI environments redirect stdin/stdout, but that doesn't affect our test runs
                // because the console is mocked, so we can skip the exception.
                if (!IsRunningCI(console))
                {
                    throw new NotSupportedException();
                }
            }

            var oldControlCAsInput = false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                PlatformWindows.Init(ref _singleton._charMap);
            }
            else
            {
                try
                {
                    oldControlCAsInput = Console.TreatControlCAsInput;
                    Console.TreatControlCAsInput = true;
                }
                catch {}
            }

            if (lastRunStatus.HasValue)
            {
                _singleton.ReportExecutionStatus(lastRunStatus.Value);
            }

            bool firstTime = true;
            while (true)
            {
                try
                {
                    if (firstTime)
                    {
                        firstTime = false;
                        _singleton.Initialize(runspace, engineIntrinsics);
                    }

                    _singleton._readLineReady = true;
                    _singleton._cancelReadCancellationToken = cancellationToken;
                    return _singleton.InputLoop();
                }
                catch (OperationCanceledException)
                {
                    // Console is either exiting or the cancellation of ReadLine has been requested
                    // by a custom PSHost implementation.
                    return "";
                }
                catch (ExitException)
                {
                    return "exit";
                }
                catch (CustomHandlerException e)
                {
                    var oldColor = console.ForegroundColor;
                    console.ForegroundColor = ConsoleColor.Red;
                    console.WriteLine(
                        string.Format(CultureInfo.CurrentUICulture, PSReadLineResources.OopsCustomHandlerException, e.InnerException.Message));
                    console.ForegroundColor = oldColor;

                    var lineBeforeCrash = _singleton._buffer.ToString();
                    _singleton.Initialize(runspace, _singleton._engineIntrinsics);
                    InvokePrompt();
                    Insert(lineBeforeCrash);
                }
                catch (Exception e)
                {
                    // If we're running tests, just throw.
                    if (_singleton._mockableMethods != _singleton)
                    {
                        throw;
                    }

                    while (e.InnerException != null)
                    {
                        e = e.InnerException;
                    }
                    var oldColor = console.ForegroundColor;
                    console.ForegroundColor = ConsoleColor.Red;
                    console.WriteLine(PSReadLineResources.OopsAnErrorMessage1);
                    console.ForegroundColor = oldColor;
                    var sb = new StringBuilder();
                    for (int i = 0; i < _lastNKeys.Count; i++)
                    {
                        sb.Append(' ');
                        sb.Append(_lastNKeys[i].KeyStr);

                        if (_singleton._dispatchTable.TryGetValue(_lastNKeys[i], out var handler) &&
                            "AcceptLine".Equals(handler.BriefDescription, StringComparison.OrdinalIgnoreCase))
                        {
                            // Make it a little easier to see the keys
                            sb.Append('\n');
                        }
                    }

                    var psVersion = PSObject.AsPSObject(engineIntrinsics.Host.Version).ToString();
                    var ourVersion = typeof(PSConsoleReadLine).Assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>().First().InformationalVersion;
                    var osInfo = RuntimeInformation.OSDescription;
                    var bufferWidth = console.BufferWidth;
                    var bufferHeight = console.BufferHeight;

                    console.WriteLine(string.Format(CultureInfo.CurrentUICulture, PSReadLineResources.OopsAnErrorMessage2,
                        ourVersion, psVersion, osInfo, bufferWidth, bufferHeight,
                        _lastNKeys.Count, sb, e));
                    var lineBeforeCrash = _singleton._buffer.ToString();
                    _singleton.Initialize(runspace, _singleton._engineIntrinsics);
                    InvokePrompt();
                    Insert(lineBeforeCrash);
                }
                finally
                {
                    _singleton._readLineReady = false;

                    try
                    {
                        // If we are closing, restoring the old console settings isn't needed,
                        // and some operating systems, it can cause a hang.
                        if (!_singleton._closingWaitHandle.WaitOne(0))
                        {
                            console.OutputEncoding = _singleton._initialOutputEncoding;

                            bool IsValid(ConsoleColor color)
                            {
                                return color >= ConsoleColor.Black && color <= ConsoleColor.White;
                            }

                            if (IsValid(_singleton._initialForeground)) {
                                console.ForegroundColor = _singleton._initialForeground;
                            }
                            if (IsValid(_singleton._initialBackground)) {
                                console.BackgroundColor = _singleton._initialBackground;
                            }
                            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                Console.TreatControlCAsInput = oldControlCAsInput;
                            }
                        }
                    }
                    catch { }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        PlatformWindows.Complete();
                    }
                }
            }
        }

        private string InputLoop()
        {
            while (true)
            {
                var killCommandCount = _killCommandCount;
                var yankCommandCount = _yankCommandCount;
                var tabCommandCount = _tabCommandCount;
                var searchHistoryCommandCount = _searchHistoryCommandCount;
                var recallHistoryCommandCount = _recallHistoryCommandCount;
                var anyHistoryCommandCount = _anyHistoryCommandCount;
                var yankLastArgCommandCount = _yankLastArgCommandCount;
                var visualSelectionCommandCount = _visualSelectionCommandCount;
                var moveToLineCommandCount = _moveToLineCommandCount;
                var moveToEndOfLineCommandCount = _moveToEndOfLineCommandCount;

                // We attempt to handle window resizing only once per a keybinding processing, because we assume the
                // window resizing cannot and shouldn't happen within the processing of a given keybinding.
                _handlePotentialResizing = true;

                try
                {
                    var key = ReadKey();
                    ProcessOneKey(key, _dispatchTable, ignoreIfNoAction: false, arg: null);
                }
                catch (LineAcceptedException)
                {
                    Debug.Assert(_inputAccepted, "LineAcceptedException should only be thrown when input was accepted within an 'OnIdle' handler.");
                }

                if (_inputAccepted)
                {
                    _acceptedCommandLine = _buffer.ToString();
                    MaybeAddToHistory(_acceptedCommandLine, _edits, _undoEditIndex);

                    _prediction.OnCommandLineAccepted(_acceptedCommandLine);
                    return _acceptedCommandLine;
                }

                if (killCommandCount == _killCommandCount)
                {
                    // Reset kill command count if it didn't change
                    _killCommandCount = 0;
                }
                if (yankCommandCount == _yankCommandCount)
                {
                    // Reset yank command count if it didn't change
                    _yankCommandCount = 0;
                }
                if (yankLastArgCommandCount == _yankLastArgCommandCount)
                {
                    // Reset yank last arg command count if it didn't change
                    _yankLastArgCommandCount = 0;
                    _yankLastArgState = null;
                }
                if (tabCommandCount == _tabCommandCount)
                {
                    // Reset tab command count if it didn't change
                    _tabCommandCount = 0;
                    _tabCompletions = null;
                }
                if (searchHistoryCommandCount == _searchHistoryCommandCount)
                {
                    if (_searchHistoryCommandCount > 0)
                    {
                        _emphasisStart = -1;
                        _emphasisLength = 0;
                        RenderWithPredictionQueryPaused();
                    }
                    _searchHistoryCommandCount = 0;
                    _searchHistoryPrefix = null;
                }
                if (recallHistoryCommandCount == _recallHistoryCommandCount)
                {
                    _recallHistoryCommandCount = 0;
                }
                if (anyHistoryCommandCount == _anyHistoryCommandCount)
                {
                    if (_anyHistoryCommandCount > 0)
                    {
                        ClearSavedCurrentLine();
                        _hashedHistory = null;
                        _currentHistoryIndex = _history.Count;
                    }
                    _anyHistoryCommandCount = 0;
                }
                if (visualSelectionCommandCount == _visualSelectionCommandCount && _visualSelectionCommandCount > 0)
                {
                    _visualSelectionCommandCount = 0;
                    RenderWithPredictionQueryPaused();  // Clears the visual selection
                }
                if (moveToLineCommandCount == _moveToLineCommandCount)
                {
                    _moveToLineCommandCount = 0;

                    if (InViCommandMode() && moveToEndOfLineCommandCount == _moveToEndOfLineCommandCount)
                    {
                        // the previous command was neither a "move to end of line" command
                        // nor a "move to line" command. In that case, the desired column
                        // number will be computed from the current position on the logical line.

                        _moveToEndOfLineCommandCount = 0;
                        _moveToLineDesiredColumn = -1;
                    }
                }
            }
        }

        T CallPossibleExternalApplication<T>(Func<T> func)
        {
            try
            {
                _console.OutputEncoding = _initialOutputEncoding;
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? PlatformWindows.CallPossibleExternalApplication(func)
                    : func();
            }
            finally
            {
                if (!_skipOutputEncodingChange)
                {
                    _console.OutputEncoding = Encoding.UTF8;
                }
            }
        }

        void CallPossibleExternalApplication(Action action)
        {
            CallPossibleExternalApplication<object>(() => { action(); return null; });
        }

        void ProcessOneKey(PSKeyInfo key, Dictionary<PSKeyInfo, KeyHandler> dispatchTable, bool ignoreIfNoAction, object arg)
        {
            var consoleKey = key.AsConsoleKeyInfo();

            // Our dispatch tables are built as much as possible in a portable way, so for example,
            // we avoid depending on scan codes like ConsoleKey.Oem6 and instead look at the
            // PSKeyInfo.Key. We also want to ignore the shift state as that may differ on
            // different keyboard layouts.
            //
            // That said, we first look up exactly what we get from Console.ReadKey - that will fail
            // most of the time, and when it does, we normalize the key.
            if (!dispatchTable.TryGetValue(key, out var handler))
            {
                // If we see a control character where Ctrl wasn't used but shift was, treat that like
                // shift hadn't be pressed.  This cleanly allows Shift+Backspace without adding a key binding.
                if (key.Shift && !key.Control && !key.Alt)
                {
                    var c = consoleKey.KeyChar;
                    if (c != '\0' && char.IsControl(c))
                    {
                        key = PSKeyInfo.From(consoleKey.Key);
                        dispatchTable.TryGetValue(key, out handler);
                    }
                }
            }

            if (handler != null)
            {
                if (handler.ScriptBlock != null)
                {
                    CallPossibleExternalApplication(() => handler.Action(consoleKey, arg));
                }
                else
                {
                    handler.Action(consoleKey, arg);
                }
            }
            else if (!ignoreIfNoAction)
            {
                SelfInsert(consoleKey, arg);
            }
        }

        static PSConsoleReadLine()
        {
            _singleton = new PSConsoleReadLine();
            _viRegister = new ViRegister(_singleton);
            InitializePropertyInfo();
        }

        private PSConsoleReadLine()
        {
            _mockableMethods = this;
            _console = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? PlatformWindows.OneTimeInit(this)
                : new VirtualTerminal();
            _charMap = new DotNetCharMap();

            _buffer = new StringBuilder(8 * 1024);
            _statusBuffer = new StringBuilder(256);
            _savedCurrentLine = new HistoryItem();
            _queuedKeys = new Queue<PSKeyInfo>();

            string hostName = null;
            // This works mostly by luck - we're not doing anything to guarantee the constructor for our
            // singleton is called on a thread with a runspace, but it is happening by coincidence.
            using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                try
                {
                    var results = ps.AddScript("[System.Diagnostics.DebuggerHidden()]param() $Host", useLocalScope: true).Invoke<PSHost>();
                    PSHost host = results.Count == 1 ? results[0] : null;
                    hostName = host?.Name;
                }
                catch
                {
                }
            }

            bool usingLegacyConsole = _console is PlatformWindows.LegacyWin32Console;
            _options = new PSConsoleReadLineOptions(hostName ?? DefaultName, usingLegacyConsole);
            _prediction = new Prediction(this);
            SetDefaultBindings(_options.EditMode);
        }

        private void Initialize(Runspace runspace, EngineIntrinsics engineIntrinsics)
        {
            _engineIntrinsics = engineIntrinsics;
            _runspace = runspace;

            // The directory separator to be used for tab completion may change depending on
            // whether we are working with a remote Runspace.
            // So, we always set it to null for every call into 'PSConsoleReadLine.ReadLine',
            // and do the real initialization when tab completion is triggered for the first
            // time during that call.
            _directorySeparator = null;

            // Update the client instance per every call to PSReadLine.
            UpdatePredictionClient(runspace, engineIntrinsics);

            if (!_delayedOneTimeInitCompleted)
            {
                DelayedOneTimeInitialize();
                _delayedOneTimeInitCompleted = true;
            }

            _buffer.Clear();
            _edits = new List<EditItem>();
            _undoEditIndex = 0;
            _editGroupStart = -1;
            _current = 0;
            _mark = 0;
            _emphasisStart = -1;
            _emphasisLength = 0;
            _ast = null;
            _tokens = null;
            _parseErrors = null;
            _inputAccepted = false;
            _initialX = _console.CursorLeft;
            _initialY = _console.CursorTop;
            _initialForeground = _console.ForegroundColor;
            _initialBackground = _console.BackgroundColor;
            _previousRender = _initialPrevRender;
            _previousRender.UpdateConsoleInfo(_console);
            _previousRender.initialY = _initialY;
            _statusIsErrorMessage = false;

            _initialOutputEncoding = _console.OutputEncoding;
            _prediction.Reset();

            // Don't change the OutputEncoding if already UTF8, no console, or using raster font on Windows
            _skipOutputEncodingChange = _initialOutputEncoding == Encoding.UTF8
                || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    && PlatformWindows.IsConsoleInput()
                    && PlatformWindows.IsUsingRasterFont());

            if (!_skipOutputEncodingChange) {
                _console.OutputEncoding = Encoding.UTF8;
            }

            _lastRenderTime = Stopwatch.StartNew();

            _killCommandCount = 0;
            _yankCommandCount = 0;
            _yankLastArgCommandCount = 0;
            _tabCommandCount = 0;
            _recallHistoryCommandCount = 0;
            _anyHistoryCommandCount = 0;
            _visualSelectionCommandCount = 0;
            _hashedHistory = null;

            if (_getNextHistoryIndex > 0)
            {
                _currentHistoryIndex = _getNextHistoryIndex;
                UpdateFromHistory(HistoryMoveCursor.ToEnd);
                _getNextHistoryIndex = 0;
                if (_searchHistoryCommandCount > 0)
                {
                    _searchHistoryPrefix = "";
                    if (Options.HistoryNoDuplicates)
                    {
                        _hashedHistory = new Dictionary<string, int>();
                    }
                }
            }
            else
            {
                _currentHistoryIndex = _history.Count;
                _searchHistoryCommandCount = 0;
            }
            if (_previousHistoryItem != null)
            {
                _previousHistoryItem.ApproximateElapsedTime = DateTime.UtcNow - _previousHistoryItem.StartTime;
            }
        }

        private void DelayedOneTimeInitialize()
        {
            // Delayed initialization is needed so that options can be set
            // after the constructor but have an affect before the user starts
            // editing their first command line.  For example, if the user
            // specifies a custom history save file, we don't want to try reading
            // from the default one.

            if (_options.MaximumHistoryCount == 0)
            {
                // Initialize 'MaximumHistoryCount' if it's not defined in user's profile.
                var historyCountVar = _engineIntrinsics?.SessionState.PSVariable.Get("MaximumHistoryCount");
                _options.MaximumHistoryCount = (historyCountVar?.Value is int historyCountValue)
                    ? historyCountValue
                    : PSConsoleReadLineOptions.DefaultMaximumHistoryCount;
            }

            if (_options.PromptText == null &&
                _engineIntrinsics?.InvokeCommand.GetCommand("prompt", CommandTypes.Function) is FunctionInfo promptCommand)
            {
                var promptIsPure = null ==
                    promptCommand.ScriptBlock.Ast.Find(ast => ast is CommandAst ||
                                                      ast is InvokeMemberExpressionAst,
                                               searchNestedScriptBlocks: true);
                if (promptIsPure)
                {
                    var res = promptCommand.ScriptBlock.InvokeReturnAsIs(Array.Empty<object>());
                    string evaluatedPrompt = res as string;
                    if (evaluatedPrompt == null && res is PSObject psobject)
                    {
                        evaluatedPrompt = psobject.BaseObject as string;
                    }
                    if (evaluatedPrompt != null)
                    {
                        int i;
                        for (i = evaluatedPrompt.Length - 1; i >= 0; i--)
                        {
                            if (!char.IsWhiteSpace(evaluatedPrompt[i])) break;
                        }

                        if (i >= 0)
                        {
                            _options.PromptText = new [] { evaluatedPrompt.Substring(i) };
                        }
                    }
                }
            }

            _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());

            _history = new HistoryQueue<HistoryItem>(Options.MaximumHistoryCount);
            _recentHistory = new HistoryQueue<string>(capacity: 5);
            _currentHistoryIndex = 0;

            bool readHistoryFile = true;
            try
            {
                if (_options.HistorySaveStyle == HistorySaveStyle.SaveNothing && Runspace.DefaultRunspace != null)
                {
                    using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
                    {
                        ps.AddCommand("Microsoft.PowerShell.Core\\Get-History");
                        foreach (var historyInfo in ps.Invoke<HistoryInfo>())
                        {
                            AddToHistory(historyInfo.CommandLine);
                        }
                        readHistoryFile = false;
                    }
                }
            }
            catch
            {
            }

            if (readHistoryFile)
            {
                ReadHistoryFile();
            }

            _killIndex = -1; // So first add indexes 0.
            _killRing = new List<string>(Options.MaximumKillRingCount);

            _readKeyWaitHandle = new AutoResetEvent(false);
            _keyReadWaitHandle = new AutoResetEvent(false);
            _closingWaitHandle = new ManualResetEvent(false);
            _requestKeyWaitHandles = new WaitHandle[] {_keyReadWaitHandle, _closingWaitHandle};
            _threadProcWaitHandles = new WaitHandle[] {_readKeyWaitHandle, _closingWaitHandle};

            // This is for a "being hosted in an alternate appdomain scenario" (the
            // DomainUnload event is not raised for the default appdomain). It allows us
            // to exit cleanly when the appdomain is unloaded but the process is not going
            // away.
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
            {
                AppDomain.CurrentDomain.DomainUnload += (x, y) =>
                {
                    _closingWaitHandle.Set();
                    _readKeyThread.Join(); // may need to wait for history to be written
                };
            }

            _readKeyThread = new Thread(ReadKeyThreadProc) {IsBackground = true, Name = "PSReadLine ReadKey Thread"};
            _readKeyThread.Start();
        }

        private static void Chord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (_singleton._chordDispatchTable.TryGetValue(PSKeyInfo.FromConsoleKeyInfo(key.Value), out var secondKeyDispatchTable))
            {
                var secondKey = ReadKey();
                _singleton.ProcessOneKey(secondKey, secondKeyDispatchTable, ignoreIfNoAction: true, arg: arg);
            }
        }

        /// <summary>
        /// Abort current action, e.g. incremental history search.
        /// </summary>
        public static void Abort(ConsoleKeyInfo? key = null, object arg = null)
        {
        }

        /// <summary>
        /// Start a new digit argument to pass to other functions.
        /// </summary>
        public static void DigitArgument(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue || char.IsControl(key.Value.KeyChar))
            {
                Ding();
                return;
            }

            if (_singleton._options.EditMode == EditMode.Vi && key.Value.KeyChar == '0')
            {
                BeginningOfLine();
                return;
            }

            bool sawDigit = false;
            _singleton._statusLinePrompt = "digit-argument: ";
            var argBuffer = _singleton._statusBuffer;
            argBuffer.Append(key.Value.KeyChar);
            if (key.Value.KeyChar == '-')
            {
                argBuffer.Append('1');
            }
            else
            {
                sawDigit = true;
            }

            _singleton.RenderWithPredictionQueryPaused(); // Render prompt
            while (true)
            {
                var nextKey = ReadKey();
                if (_singleton._dispatchTable.TryGetValue(nextKey, out var handler))
                {
                    if (handler.Action == DigitArgument)
                    {
                        if (nextKey.KeyChar == '-')
                        {
                            if (argBuffer[0] == '-')
                            {
                                argBuffer.Remove(0, 1);
                            }
                            else
                            {
                                argBuffer.Insert(0, '-');
                            }
                            _singleton.RenderWithPredictionQueryPaused(); // Render prompt
                            continue;
                        }

                        if (nextKey.KeyChar >= '0' && nextKey.KeyChar <= '9')
                        {
                            if (!sawDigit && argBuffer.Length > 0)
                            {
                                // Buffer is either '-1' or '1' from one or more Alt+- keys
                                // but no digits yet.  Remove the '1'.
                                argBuffer.Length -= 1;
                            }
                            sawDigit = true;
                            argBuffer.Append(nextKey.KeyChar);
                            _singleton.RenderWithPredictionQueryPaused(); // Render prompt
                            continue;
                        }
                    }
                    else if (handler.Action == Abort ||
                             handler.Action == CancelLine ||
                             handler.Action == CopyOrCancelLine)
                    {
                        break;
                    }
                }

                if (int.TryParse(argBuffer.ToString(), out var intArg))
                {
                    _singleton.ProcessOneKey(nextKey, _singleton._dispatchTable, ignoreIfNoAction: false, arg: intArg);
                }
                else
                {
                    Ding();
                }
                break;
            }

            // Remove our status line
            argBuffer.Clear();
            _singleton.ClearStatusMessage(render: true);
        }


        /// <summary>
        /// Erases the current prompt and calls the prompt function to redisplay
        /// the prompt.  Useful for custom key handlers that change state, e.g.
        /// change the current directory.
        /// </summary>
        public static void InvokePrompt(ConsoleKeyInfo? key = null, object arg = null)
        {
            var console = _singleton._console;

            if (arg is int newY)
            {
                if (newY < 0 || newY >= console.BufferHeight)
                {
                    throw new ArgumentOutOfRangeException(nameof(arg));
                }

                console.CursorVisible = false;
                console.SetCursorPosition(0, newY);
            }
            else
            {
                newY = _singleton._initialY - _singleton._options.ExtraPromptLineCount;

                // Silently return if user has implicitly requested an impossible prompt invocation.
                if (newY < 0)
                {
                    return;
                }

                console.CursorVisible = false;
                console.SetCursorPosition(0, newY);

                // We need to rewrite the prompt, so blank out everything from a previous prompt invocation
                // in case the next one is shorter.
                var spaces = Spaces(console.BufferWidth);
                for (int i = 0; i < _singleton._options.ExtraPromptLineCount + 1; i++)
                {
                    console.Write(spaces);
                }

                console.SetCursorPosition(0, newY);
            }

            string newPrompt = GetPrompt();

            console.Write(newPrompt);
            _singleton._initialX = console.CursorLeft;
            _singleton._initialY = console.CursorTop;
            _singleton._previousRender = _initialPrevRender;
            _singleton._previousRender.UpdateConsoleInfo(console);
            _singleton._previousRender.initialY = _singleton._initialY;

            _singleton.Render();
            console.CursorVisible = true;
        }

        private static string GetPrompt()
        {
            string newPrompt = null;

            try
            {
                if (_singleton._runspace?.Debugger != null && _singleton._runspace.Debugger.InBreakpoint)
                {
                    // Run prompt command in debugger API to ensure it is run correctly on the runspace.
                    // This handles remote runspace debugging and nested debugger scenarios.
                    PSDataCollection<PSObject> results = new PSDataCollection<PSObject>();
                    var command = new PSCommand();
                    command.AddCommand("prompt");
                    _singleton._runspace.Debugger.ProcessCommand(
                        command,
                        results);

                    if (results.Count == 1)
                        newPrompt = results[0].BaseObject as string;
                }
                else
                {
                    var runspaceIsRemote = _singleton._mockableMethods.RunspaceIsRemote(_singleton._runspace);

                    System.Management.Automation.PowerShell ps;
                    if (!runspaceIsRemote)
                    {
                        ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
                    }
                    else
                    {
                        ps = System.Management.Automation.PowerShell.Create();
                        ps.Runspace = _singleton._runspace;
                    }

                    using (ps)
                    {
                        ps.AddCommand("prompt");
                        var result = ps.Invoke<string>();
                        if (result.Count == 1)
                        {
                            newPrompt = result[0];

                            if (runspaceIsRemote)
                            {
                                if (!string.IsNullOrEmpty(_singleton._runspace?.ConnectionInfo?.ComputerName))
                                {
                                    newPrompt = "[" + (_singleton._runspace?.ConnectionInfo).ComputerName + "]: " + newPrompt;
                                }
                            }
                        }
                    }
                }

            }
            catch
            {
                // Catching all exceptions makes debugging problems a bit harder, but it avoids some noise if
                // the remote doesn't define a prompt.
            }

            if (string.IsNullOrEmpty(newPrompt))
                newPrompt = "PS>";

            return newPrompt;
        }

        internal static bool IsRunningCI(IConsole console)
        {
            Type consoleType = console.GetType();
            return consoleType.FullName == "Test.TestConsole"
                || consoleType.BaseType.FullName == "Test.TestConsole";
        }
    }
}

/*
 * PROJECT:     ReactOS System Regression Testing Utility, Windows/VirtualBox version
 * LICENSE:     GNU GPLv2 or any later version as published by the Free Software Foundation
 * PURPOSE:     Processing the incoming debugging data
 * COPYRIGHT:   Copyright Aleksey Bragin <aleksey@reactos.org>. Based around ideas and code from
 *              sysreg created by Christoph von Wittich <christoph_vw@reactos.org> and
 *              Colin Finck <colin@reactos.org>
 */

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.IO;
using VirtualBox;
using System.Runtime.InteropServices;

namespace sysreg3
{
    public class LogReader
    {
        NamedPipeClientStream pipe;
        string debugLogFilename;
        ISession vmSession;
        int stageNum;
        int timeOut;
        const string KDBPROMPT = "kdb:>";
        const string KDBBTCONT = "--- Press q";
        const string KDBASSERT = "Break repea";

        String[] stageCheckpoint;

        private ContinueType _result;
        private bool _timedOut;
        private AutoResetEvent _timeOutEvent;
        Timer watchdog;

        public LogReader(string namedPipeName, string logName, ISession session, int stage, int vmTimeout)
        {
            debugLogFilename = logName;
            vmSession = session;
            stageNum = stage;
            timeOut = vmTimeout;
            _timedOut = true;

            stageCheckpoint = new String[3];
            stageCheckpoint[0] = "It's the final countdown...";
            stageCheckpoint[1] = "It's the final countdown...";
            stageCheckpoint[2] = "SYSREG_CHECKPOINT:THIRDBOOT_COMPLETE";

            _result = ContinueType.EXIT_DONT_CONTINUE;

            _timeOutEvent = new AutoResetEvent(false);
            watchdog = new Timer(WatchdogCallback, _timeOutEvent, timeOut, Timeout.Infinite);

            // Connect to the named pipe of the VM to receive its debug info.
            pipe = new NamedPipeClientStream("localhost", namedPipeName, PipeDirection.InOut);
        }

        public void WatchdogCallback(Object stateInfo)
        {
            AutoResetEvent autoEvent = (AutoResetEvent)stateInfo;

            // Signal the event
            autoEvent.Set();
        }

        public void Run()
        {
            try
            {
                using (StreamReader sr = new StreamReader(pipe))
                using (TextWriter debugLogWriter = 
                    (debugLogFilename != null) ? new StreamWriter(debugLogFilename, false) : null)
                {
                    pipe.Connect(3000);

                    string line, cacheLine = "";
                    int kdbgHit = 0, cacheHits = 0;
                    bool quitLoop = false;
                    bool kdserial = false;

                    while (!quitLoop)
                    {
                        /* Read up to 512 chars */
                        StringBuilder buffer = new StringBuilder(512);
                        int index = 0, read;
                        while ((read = sr.Read()) != -1)
                        {
                            buffer.Append((char)read);
                            /* Break on newlines or in case of KDBG messages (which aren't terminated by newlines) */
                            if (read == (int)'\n')
                                break;
                            if (buffer.ToString().Contains(KDBPROMPT) 
                                || buffer.ToString().Contains(KDBBTCONT)
                                || buffer.ToString().Contains(KDBASSERT))
                                break;
                            index++;
                            if (index >= buffer.Capacity)
                                break;
                        }

                        line = buffer.ToString();
                        {
                            /* Reset the watchdog timer */
                            watchdog.Change(timeOut, Timeout.Infinite);

                            Console.Write(line);
                            if(debugLogWriter != null)
                                debugLogWriter.Write(line);

                            /* Detect whether the same line appears over and over again.
                               If that is the case, cancel this test after a specified number of repetitions. */
                            if (line == cacheLine)
                            {
                                cacheHits++;

                                if (cacheHits > RegTester.maxCacheHits)
                                {
                                    Console.WriteLine("[SYSREG] Test seems to be stuck in an endless loop, canceled!\n");
                                    _result = ContinueType.EXIT_CONTINUE;
                                    quitLoop = true;
                                    break;
                                }
                            }
                            else
                            {
                                cacheHits = 0;
                                cacheLine = line;
                            }

                            /* Check for magic sequences */
                            if (line.Contains("KDSERIAL"))
                            {
#if TRACE
                                Console.WriteLine("[SYSREG] Switching to kdserial mode");
#endif
                                kdserial = true;
                                continue;
                            }
                            if (line.Contains(KDBPROMPT))
                            {
#if TRACE
                                Console.Write("-[TRACE] kdb prompt hit-");
#endif
                                kdbgHit++;

                                if (kdbgHit == 1)
                                {
                                    /* It happened for the first time, backtrace */
                                    if (kdserial)
                                    {
                                        char[] bt = { 'b', 't', '\r' };
                                        foreach (char c in bt)
                                            pipe.WriteByte((byte)c);
                                    }
                                    else
                                    {
                                        vmSession.Console.Keyboard.PutScancode(0x30); // b make
                                        vmSession.Console.Keyboard.PutScancode(0xb0); // b release
                                        vmSession.Console.Keyboard.PutScancode(0x14); // t make
                                        vmSession.Console.Keyboard.PutScancode(0x94); // t release
                                        vmSession.Console.Keyboard.PutScancode(0x1c); // Enter make
                                        vmSession.Console.Keyboard.PutScancode(0x9c); // Enter release
                                    }

                                    continue;
                                }
                                else
                                {
                                    /* It happened once again, no reason to continue */
                                    Console.WriteLine();
                                    _result = ContinueType.EXIT_CONTINUE;
                                    quitLoop = true;
                                    break;
                                }
                            }
                            else if (line.Contains(KDBBTCONT))
                            {
                                /* Send Return to get more data from Kdbg */
                                if (kdserial)
                                {
                                    pipe.WriteByte((byte)'\r');
                                }
                                else
                                {
                                    vmSession.Console.Keyboard.PutScancode(0x1c); // Enter make
                                    vmSession.Console.Keyboard.PutScancode(0x9c); // Enter release
                                }
                                continue;
                            }
                            else if (line.Contains(KDBASSERT))
                            {
                                /* Break once */
                                if (kdserial)
                                {
                                    pipe.WriteByte((byte)'o');
                                }
                                else
                                {
                                    vmSession.Console.Keyboard.PutScancode(0x18); // 'O' make
                                    vmSession.Console.Keyboard.PutScancode(0x98); // 'O' release

                                }
                            }
                            else if (line.Contains("SYSREG_ROSAUTOTEST_FAILURE"))
                            {
                                quitLoop = true;
                                break;
                            }
                            else if (line.Contains(stageCheckpoint[stageNum]))
                            {
                                _result = ContinueType.EXIT_CHECKPOINT_REACHED;
                                quitLoop = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Let the user know what went wrong.
                Console.WriteLine("Exception occured in the LogReader.Run():");
                Console.WriteLine(e.Message);
            }
            finally
            {
                pipe.Close();
                Thread.Sleep(1000);
            }

            // Signal that we're done and it's not a timed out state
            _timedOut = false;
            _timeOutEvent.Set();
        }

        public ContinueType Result
        {
            get
            {
                return _result;
            }
        }
        public AutoResetEvent TimeOutEvent
        {
            get
            {
                return _timeOutEvent;
            }
        }
        public bool TimedOut
        {
            get
            {
                return _timedOut;
            }
        }
    }
}

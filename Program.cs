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
    public enum ContinueType
    {
        EXIT_CHECKPOINT_REACHED,
        EXIT_CONTINUE,
        EXIT_DONT_CONTINUE
    }

    class Program
    {
        static void Main(string[] args)
        {
            Environment.ExitCode = 1;
            RegTester regTester = new RegTester();

            // Set parameters
            for (int i = 0; i < args.Length; i++)
            {
                int intValue;
                switch (args[i])
                {
                    case "--maxretries" :
                        if (i < args.Length - 1 && Int32.TryParse(args[i + 1], out intValue))
                            { regTester.maxRetries = intValue; i++; }
                        break;
                    case "--sessiontimeout":
                        if (i < args.Length - 1 && Int32.TryParse(args[i + 1], out intValue))
                            { regTester.vmTimeout = intValue * 1000; i++; }
                        break;
                    case "--nolog":
                        regTester.logName = null;
                        break;
                    case "--maxcachehits":
                        if (i < args.Length - 1 && Int32.TryParse(args[i + 1], out intValue))
                        { RegTester.maxCacheHits = intValue ; i++; }
                        break;
                }
            }
            if(regTester.debugLogFilename != null)
                Console.WriteLine("[SYSREG] Serial log path: " + regTester.debugLogFilename);

            regTester.RunTests();
        }
    }
}

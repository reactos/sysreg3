﻿/*
 * PROJECT:     ReactOS System Regression Testing Utility, Windows/VirtualBox version
 * LICENSE:     GNU GPLv2 or any later version as published by the Free Software Foundation
 * PURPOSE:     Processing the incoming debugging data
 * COPYRIGHT:   Copyright Aleksey Bragin <aleksey@reactos.org>. Based around ideas and code from
 *              sysreg created by Christoph von Wittich <christoph_vw@reactos.org> and
 *              Colin Finck <colin@reactos.org>
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using VirtualBox;

namespace sysreg3
{
    class RegTester
    {
        string machineName = "ReactOS Testbot";
        string dbgPortPath = "C:\\testbot.txt";
        int maxRetries = 30;
        int numStages = 3;
        int vmTimeout = 60 * 1000; // 60 secs

        Int64 hddSize = (Int64)2048 * 1024 * 1024;

        IMachine rosVM;
        IVirtualBox vBox;

        enum ContinueType
        {
            EXIT_CHECKPOINT_REACHED,
            EXIT_CONTINUE,
            EXIT_DONT_CONTINUE
        }

        String[] stageCheckpoint;

        public RegTester()
        {
            /* Create VBox instance */
            vBox = new VirtualBox.VirtualBox();

            stageCheckpoint = new String[3];
            stageCheckpoint[0] = "It's the final countdown...";
            stageCheckpoint[1] = "It's the final countdown...";
            stageCheckpoint[2] = "SYSREG_CHECKPOINT:THIRDBOOT_COMPLETE";

            /* Get temp dir location and construct serial log path */
            string tempPath = Path.GetFullPath(Environment.CurrentDirectory);
            dbgPortPath = tempPath + "\\testbot.txt";
            Console.WriteLine("[SYSREG] Serial log path: " + dbgPortPath);
        }

        private ContinueType ProcessDebugOutput(ISession vmSession, int stage)
        {
            ContinueType Result = ContinueType.EXIT_DONT_CONTINUE;

            /* Get the serial port */
            ISerialPort dbgPort = vmSession.Machine.GetSerialPort(0);

            Stream stream = new FileStream(dbgPort.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            StreamReader sr = new StreamReader(stream);

            try
            {
                string line;
                int kdbgHit = 0;
                bool quitLoop = false;

                while (!quitLoop)
                {
                    /* Poll the stream every 1 sec */
                    int waitingTime = 0;
                    while (sr.EndOfStream)
                    {
                        waitingTime += 1000;
                        Thread.Sleep(1000);

                        /* Peek a byte to update the EndOfStream value */
                        sr.Peek();

                        if (waitingTime >= vmTimeout)
                        {
                            /* We hit the timeout, quit */
                            Console.WriteLine("[SYSREG] timeout");
                            Result = ContinueType.EXIT_CONTINUE;
                            quitLoop = true;
                            break;
                        }
                    }

                    while ((line = sr.ReadLine()) != null)
                    {
                        Console.WriteLine(line);

                        /* Check for magic sequences */
                        if (line.Contains("kdb:>"))
                        {
                            kdbgHit++;

                            if (kdbgHit == 1)
                            {
                                /* It happened for the first time, backtrace */
                                vmSession.Console.Keyboard.PutScancode(0x30); // b make
                                vmSession.Console.Keyboard.PutScancode(0xb0); // b release
                                vmSession.Console.Keyboard.PutScancode(0x14); // t make
                                vmSession.Console.Keyboard.PutScancode(0x94); // t release
                                vmSession.Console.Keyboard.PutScancode(0x1c); // Enter make
                                vmSession.Console.Keyboard.PutScancode(0x9c); // Enter release

                                continue;
                            }
                            else
                            {
                                /* It happened once again, no reason to continue */
                                Console.WriteLine();
                                Result = ContinueType.EXIT_CONTINUE;
                                quitLoop = true;
                                break;
                            }
                        }
                        else if (line.Contains("--- Press q"))
                        {
                            /* Send Return to get more data from Kdbg */
                            vmSession.Console.Keyboard.PutScancode(0x1c); // Enter make
                            vmSession.Console.Keyboard.PutScancode(0x9c); // Enter release
                            continue;
                        }
                        else if (line.Contains("SYSREG_ROSAUTOTEST_FAILURE"))
                        {
                            quitLoop = true;
                            break;
                        }
                        else if (line.Contains(stageCheckpoint[stage]))
                        {
                            Result = ContinueType.EXIT_CHECKPOINT_REACHED;
                            quitLoop = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Let the user know what went wrong.
                Console.WriteLine("The file could not be read:");
                Console.WriteLine(e.Message);
            }
            finally
            {
                sr.Close();
            }

            return Result;
        }

        private void CreateHardDisk(Session vmSession, IStorageController controller, int dev, int port)
        {
            IMedium rosHdd;
            IProgress progress;

            string curDir = Path.GetFullPath(Environment.CurrentDirectory);

            /* Create the hdd and storage */
            rosHdd = vBox.CreateHardDisk(null, curDir + "\\ReactOS Testbot.vdi");
            progress = rosHdd.CreateBaseStorage(hddSize, (uint)MediumVariant.MediumVariant_Standard);
            progress.WaitForCompletion(-1);

            //String errStr;
            //progress.ErrorInfo.GetDescription(out errStr);
            //Console.WriteLine(errStr);

            /* FIXME: Make sure there is no hdd with the same name hanging around in registered condition */

            /* Attach it to the vm */
            vmSession.Machine.SaveSettings();
            vmSession.Machine.AttachDevice(controller.Name, port, dev, DeviceType.DeviceType_HardDisk, rosHdd);
            vmSession.Machine.SaveSettings();
        }

        private void EmptyHardDisk(Session vmSession)
        {
            IProgress progress;
            IStorageController controller = null;
            uint inst;
            IMedium rosHdd = null;
            int dev = 0, port;
            Boolean HddFound = false;

            /* Go through storage controllers to find IDE/SATA one */
            for (inst = 0; inst < 4; inst++)
            {
                try
                {
                    controller = rosVM.GetStorageControllerByInstance(inst);
                    if (controller.Bus == StorageBus.StorageBus_IDE ||
                        controller.Bus == StorageBus.StorageBus_SATA)
                        break;
                }
                catch (Exception exc)
                {
                    /* Just skip it */
                }
            }

            /* Now check what HDD we have connected to this controller */
            for (port = 0; port < controller.MaxPortCount; port++)
            {
                for (dev = 0; dev < controller.MaxDevicesPerPortCount; dev++)
                {
                    try
                    {
                        rosHdd = rosVM.GetMedium(controller.Name, port, dev);
                        if (rosHdd.DeviceType == DeviceType.DeviceType_HardDisk)
                        {
                            /* We found the one and only harddisk */
                            HddFound = true;
                            break;
                        }
                        rosHdd.Close();
                    }
                    catch (Exception exc)
                    {
                        /* Just skip it */
                    }
                }

                if (HddFound) break;
            }

            if (HddFound)
            {
                /* Delete it and create a new one */
                vmSession.Machine.DetachDevice(controller.Name, port, dev);
                vmSession.Machine.SaveSettings();

                progress = rosHdd.DeleteStorage();
                progress.WaitForCompletion(-1);
                rosHdd.Close();
            }
            else
            {
                /* FIXME: We should find a free slot for the HDD */
                port = 0;
                dev = 0;
            }

            /* Create a new one */
            CreateHardDisk(vmSession, controller, port, dev);
        }

        private void EmptyDebugLog(Session vmSession)
        {
            try
            {
                FileStream dbgFile = File.Open(dbgPortPath, FileMode.Truncate);
                dbgFile.Close();
            }
            catch
            {
                /* Don't care about the exceptions here */
            }
        }

        private void ConfigVm(Session vmSession)
        {
            /* Check serial port */
            ISerialPort dbgPort = vmSession.Machine.GetSerialPort(0);

            if (dbgPort.Enabled == 0)
            {
                /* Create it */
                dbgPort.Enabled = 1;
                dbgPort.Path = dbgPortPath;
                dbgPort.HostMode = PortMode.PortMode_RawFile;
            }
            else
            {
                /* Ensure it's set to the most up to date path */
                dbgPort.Path = dbgPortPath;
            }
        }

        public void RunTests()
        {
            ContinueType Ret = ContinueType.EXIT_DONT_CONTINUE;
            IProgress vmProgress;

            // TODO: Load settings

            /* Open the testing machine */
            Session vmSession = new Session();

            try
            {
                rosVM = vBox.FindMachine(machineName);
            }
            catch (Exception exc)
            {
                /* Opening failed. Probably we need to create it */
                Console.WriteLine("Opening the vm failed " + exc.ToString());
                return;
            }

            rosVM.LockMachine(vmSession, LockType.LockType_Write);

            /* Configure the virtual machine */
            ConfigVm(vmSession);

            /* Empty or create the HDD, prepare for the first run */
            EmptyHardDisk(vmSession);

            /* Close VM session */
            vmSession.UnlockMachine();

            /* Empty the debug log file */
            EmptyDebugLog(vmSession);

            /* Start main testing loop */
            for (int stage = 0; stage < numStages; stage++)
            {
                int retries;
                for (retries = 0; retries < maxRetries; retries++)
                {
                    /* Start the VM */
                    try
                    {
                        vmProgress = rosVM.LaunchVMProcess(vmSession, "gui", null);
                        vmProgress.WaitForCompletion(-1);

                        if (vmProgress.ResultCode != 0)
                        {
                            /* Print out error's text */
                            Console.WriteLine("Error starting VM: " + vmProgress.ErrorInfo.Text);

                            /* Close VM session */
                            vmSession.UnlockMachine();
                            break;
                        }

                        Console.WriteLine(); Console.WriteLine(); Console.WriteLine();
                        Console.WriteLine("[SYSREG] Running stage {0}...", stage + 1);
                        Console.WriteLine("[SYSREG] Domain {0} started.\n", rosVM.Name);

                        Ret = ProcessDebugOutput(vmSession, stage);

                        /* Kill the VM */
                        vmProgress = vmSession.Console.PowerDown();
                        vmProgress.WaitForCompletion(-1);

                        try
                        {
                            /* Close the VM session without paying attention to any problems */
                            vmSession.UnlockMachine();

                            /* Wait till the machine state is actually closed (no vmProgress alas) */
                            int waitingTimeout = 0;
                            while (vmSession.State != SessionState.SessionState_Unlocked ||
                                   waitingTimeout < 5)
                            {
                                Thread.Sleep(1000);
                                waitingTimeout++;
                            }
                        }
                        catch
                        {
                        }

                        /* If we have a checkpoint to reach for success, assume that
                           the application used for running the tests (probably "rosautotest")
                           continues with the next test after a VM restart. */
                        if (stage == 2 && Ret == ContinueType.EXIT_CONTINUE)
                            Console.WriteLine("[SYSREG] Rebooting VM (retry {0})", retries + 1);
                        else
                        {
                            /* Empty the debug log file */
                            EmptyDebugLog(vmSession);
                            break;
                        }
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("[SYSREG] Running the VM failed with exception: " + exc.ToString());
                        //break;
                    }
                }

                /* Check for a maximum number of retries */
                if (retries == maxRetries)
                {
                    Console.WriteLine("[SYSREG] Maximum number of allowed retries exceeded, aborting!");
                    break;
                }

                /* Stop executing if asked so */
                if (Ret == ContinueType.EXIT_DONT_CONTINUE) break;
            }

            switch (Ret)
            {
                case ContinueType.EXIT_CHECKPOINT_REACHED:
                    Console.WriteLine("[SYSREG] Status: Reached the checkpoint!");
                    break;
                case ContinueType.EXIT_CONTINUE:
                    Console.WriteLine("[SYSREG] Status: Failed to reach the checkpoint!!");
                    break;
                case ContinueType.EXIT_DONT_CONTINUE:
                    Console.WriteLine("[SYSREG] Status: Testing process aborted!");
                    break;
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            RegTester regTester = new RegTester();
            regTester.RunTests();
        }
    }
}

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
    class RegTester
    {
        const string machineName = "ReactOS Testbot";
        const string diskFileName = "ReactOS Testbot.vdi";
        public int maxRetries = 30;
        public static int maxCacheHits = 1000;
        const int numStages = 3;
        public int vmTimeout = 60 * 1000; // 60 secs
        const Int64 hddSize = (Int64)2048 * 1024 * 1024;
        const string namedPipeName = @"reactos\testbot";
        const string defaultLogName = "testbot.txt";

        readonly string vmBaseFolder; // Directory where VM will be created if it doesn't exist yet
        public string logName;
        IMachine rosVM;
        IVirtualBox vBox;

        public RegTester()
        {
            /* Create VBox instance */
            vBox = new VirtualBox.VirtualBox();
            vmBaseFolder = Path.Combine(Environment.CurrentDirectory, "vm");
            logName = defaultLogName;
        }
        public string debugLogFilename
        {
            get
            {
                if (logName != null)
                    return Path.Combine(Environment.CurrentDirectory, logName);
                else
                    return null;
            }
        }

        private ContinueType ProcessDebugOutput(ISession vmSession, int stage)
        {
            ContinueType Result = ContinueType.EXIT_DONT_CONTINUE;
            bool noTimeout;

            LogReader logReader = new LogReader(namedPipeName, debugLogFilename, vmSession, stage, vmTimeout);

            /* Start the logger thread */
            Thread t = new Thread(new ThreadStart(logReader.Run));
            t.Start();

            /* Wait until it terminates or exits with a timeout */
            logReader.TimeOutEvent.WaitOne();

            /* Get the result */
            Result = logReader.Result;

            if (logReader.TimedOut)
            {
                /* We hit the timeout, quit */
                Console.WriteLine("[SYSREG] timeout");
                Result = ContinueType.EXIT_CONTINUE;

                /* Force thread termination */
                t.Abort();
            }

            return Result;
        }

        private void CreateHardDisk(Session vmSession, IStorageController controller, int dev, int port)
        {
            IMedium rosHdd;
            IProgress progress;

            string curDir = Path.GetFullPath(Environment.CurrentDirectory);

            /* Create the hdd and storage */
            rosHdd = vBox.CreateHardDisk(null, Path.Combine(curDir, diskFileName));
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

            /* Delete the existing hdd */
            if (HddFound)
            {
                try
                {
                    controller = rosVM.GetStorageControllerByInstance(inst);
                    vmSession.Machine.DetachDevice(controller.Name, port, dev);
                    vmSession.Machine.SaveSettings();

                    progress = rosHdd.DeleteStorage();
                    progress.WaitForCompletion(-1);
                    rosHdd.Close();
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Could not delete existing HDD:" + exc);
                }
            }
            else
            {
                /* Connect to port 0, device 0 if there was no hdd found */
                port = 0;
                dev = 0;
            }

            /* Create a new one */
            CreateHardDisk(vmSession, controller, port, dev);
        }

        private void EmptyDebugLog()
        {
            try
            {
                FileStream dbgFile = File.Open(debugLogFilename, FileMode.Truncate);
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

            // Path must be set BEFORE setting HostMode!
            dbgPort.Path = @"\\.\pipe\" + namedPipeName; // Name must always have this special prefix
            dbgPort.HostMode = PortMode.PortMode_HostPipe;
            dbgPort.Server = 1;
            dbgPort.Enabled = 1;
        }

        private IMachine CreateVm()
        {
            IMachine vm = null;
            IStorageController hddController;

            try
            {
                Console.WriteLine("[SYSREG] creating VM");
                // For allowed OS type values, query IVirtualBox.GuestOSTypes and look for "id" field
                vm = vBox.CreateMachine(Path.Combine(vmBaseFolder, machineName + ".vbox"), machineName, null, "Windows2003", "");
                hddController = vm.AddStorageController("sata0", StorageBus.StorageBus_SATA);
                vm.MemorySize = 256; // In MB
                vm.VRAMSize = 16; // In MB
                vm.SaveSettings();

                Console.WriteLine("[SYSREG] registering VM");
                vBox.RegisterMachine(vm);
            }
            catch (Exception exc)
            {
                // Creation failed.
                Console.WriteLine("Creating the VM failed: " + exc);
            }
            return vm;
        }

        public void RunTests()
        {
            ContinueType ret = ContinueType.EXIT_DONT_CONTINUE;
            IProgress vmProgress;

            // TODO: Load settings

            /* Open the testing machine */
            Session vmSession = new Session();

            try
            {
                rosVM = vBox.FindMachine(machineName);
            }
            catch (COMException exc)
            {                
                /* Opening failed. Probably we need to create it */
                Console.WriteLine("Opening the vm failed: " + exc);

                rosVM = CreateVm();
            }

            rosVM.LockMachine(vmSession, LockType.LockType_Write);

            /* Configure the virtual machine */
            ConfigVm(vmSession);

            /* Empty or create the HDD, prepare for the first run */
            EmptyHardDisk(vmSession);

            /* Close VM session */
            vmSession.UnlockMachine();

            /* Empty the debug log file */
            if(logName != null)
                EmptyDebugLog();

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
                            if(vmSession.State == SessionState.SessionState_Locked)
                                vmSession.UnlockMachine();
                            break;
                        }

                        Console.WriteLine(); Console.WriteLine(); Console.WriteLine();
                        Console.WriteLine("[SYSREG] Running stage {0}...", stage + 1);
                        Console.WriteLine("[SYSREG] Domain {0} started.\n", rosVM.Name);

                        ret = ProcessDebugOutput(vmSession, stage);

                        /* Kill the VM if it's not already powered off */
                        if (vmSession.State != SessionState.SessionState_Unlocked
                            && rosVM.State >= MachineState.MachineState_FirstOnline
                            && rosVM.State <= MachineState.MachineState_LastOnline)
                        {
#if TRACE
                            Console.WriteLine("[SYSREG] Killing VM (state " + rosVM.State.ToString()+")");
#endif
                            try
                            {
                                vmProgress = vmSession.Console.PowerDown();
                                vmProgress.WaitForCompletion(-1);
                            }
                            catch (System.Runtime.InteropServices.COMException comEx)
                            {
                                Console.WriteLine("[SYSREG] Failed to shutdown VM: " + comEx.ToString());
                                if (rosVM.State != MachineState.MachineState_PoweredOff)
                                    throw;
                            }
                        }

                        try
                        {
                            /* Close the VM session without paying attention to any problems */
                            if (vmSession.State == SessionState.SessionState_Locked)
                            {
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
                        }
                        catch
                        {
                        }

                        /* If we have a checkpoint to reach for success, assume that
                           the application used for running the tests (probably "rosautotest")
                           continues with the next test after a VM restart. */
                        if (stage == 2 && ret == ContinueType.EXIT_CONTINUE)
                            Console.WriteLine("[SYSREG] Rebooting VM (retry {0})", retries + 1);
                        else
                        {
                            /* Empty the debug log file */
                            if(logName != null)
                                EmptyDebugLog();
                            break;
                        }
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("[SYSREG] Running the VM failed with exception: " + exc);
                        //break;
                    }
                }

                /* Check for a maximum number of retries */
                if (retries >= maxRetries)
                {
                    Console.WriteLine("[SYSREG] Maximum number of allowed retries exceeded, aborting!");
                    break;
                }

                /* Stop executing if asked so */
                if (ret == ContinueType.EXIT_DONT_CONTINUE) break;
            }

            switch (ret)
            {
                case ContinueType.EXIT_CHECKPOINT_REACHED:
                    Console.WriteLine("[SYSREG] Status: Reached the checkpoint!");
                    Environment.ExitCode = 0;
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
}

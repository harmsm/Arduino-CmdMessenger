﻿#region CmdMessenger - MIT - (c) 2014 Thijs Elenbaas.
/*
  CmdMessenger - library that provides command based messaging

  Permission is hereby granted, free of charge, to any person obtaining
  a copy of this software and associated documentation files (the
  "Software"), to deal in the Software without restriction, including
  without limitation the rights to use, copy, modify, merge, publish,
  distribute, sublicense, and/or sell copies of the Software, and to
  permit persons to whom the Software is furnished to do so, subject to
  the following conditions:

  The above copyright notice and this permission notice shall be
  included in all copies or substantial portions of the Software.

  Copyright 2014 - Thijs Elenbaas
*/
#endregion

using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Forms;

namespace CommandMessenger
{
    public class ConnectionManagerProgressEventArgs : EventArgs
    {
        public int Level { get; set; }
        public String Description { get; set; }
    }

    public enum ConnectionManagerStates
    {
        Scan,
        Connect,
        Watchdog,
        Wait,
        Stop
    }

    public abstract class ConnectionManager : IDisposable 
    {
        protected Control ControlToInvokeOn;
        protected readonly CmdMessenger CmdMessenger;
        protected ConnectionManagerStates ConnectionManagerState;

        public event EventHandler ConnectionTimeout;
        public event EventHandler ConnectionFound;
        public event EventHandler<ConnectionManagerProgressEventArgs> Progress;

        private readonly BackgroundWorker _workerThread;
        private readonly int _challengeCommandId;
        private readonly int _responseCommandId;

        private long _lastCheckTime;
        private long _nextTimeOutCheck;
        private uint _watchdogTries;

        public bool Connected { get; protected set; }

        public int WatchdogTimeout { get; set; }
        public int WatchdogRetryTimeout { get; set; }
        public uint MaxWatchdogTries { get; set; }
        public bool WatchdogEnabled { get; set; }
        public bool UseFixedPort { get; set; }

        protected ConnectionManager(CmdMessenger cmdMessenger, int challengeCommandId, int responseCommandId)
        {
            if (cmdMessenger == null)
                throw new ArgumentNullException("cmdMessenger", "Command Messenger is null.");
            
            WatchdogTimeout = 2000;
            WatchdogRetryTimeout = 1000;        
            MaxWatchdogTries = 3;

            UseFixedPort = false;
            
            ControlToInvokeOn = null;
            CmdMessenger = cmdMessenger;

            _workerThread = new BackgroundWorker { WorkerSupportsCancellation = true, WorkerReportsProgress = false };

            _challengeCommandId = challengeCommandId;
            _responseCommandId = responseCommandId;

            CmdMessenger.Attach(responseCommandId, OnResponseCommandId);
        }

        /// <summary>
        /// Start connection manager. Will set up thread, but will not start scanning
        /// </summary>
        protected void StartConnectionManager()
        {
            _workerThread.DoWork += WorkerThreadDoWork;

            ConnectionManagerState = ConnectionManagerStates.Wait;
            if (!_workerThread.IsBusy)
            {
                // Start the asynchronous operation.
                _workerThread.RunWorkerAsync();
            }
        }

        /// <summary>
        /// Stop connection manager.
        /// </summary>
        protected void StopConnectionManager()
        {
            ConnectionManagerState = ConnectionManagerStates.Stop;

            if (_workerThread.WorkerSupportsCancellation)
            {
                // Cancel the asynchronous operation.
                _workerThread.CancelAsync();
            }
            _workerThread.DoWork -= WorkerThreadDoWork;
        }

        /// <summary> Sets a control to invoke on. </summary>
        /// <param name="controlToInvokeOn"> The control to invoke on. </param>
        public void SetControlToInvokeOn(Control controlToInvokeOn)
        {
            ControlToInvokeOn = controlToInvokeOn;
        }

        protected void InvokeEvent(EventHandler eventHandler)
        {
            try
            {
                if (eventHandler == null) return;
                if (ControlToInvokeOn != null && ControlToInvokeOn.InvokeRequired)
                {
                    //Asynchronously call on UI thread
                    ControlToInvokeOn.BeginInvoke((MethodInvoker) (() => eventHandler(this, null)));
                    Thread.Yield();
                }
                else
                {
                    //Directly call
                    eventHandler(this, null);
                }
            }
            catch (Exception)
            {
            }
        }

        protected void ConnectionFoundEvent()
        {
            ConnectionManagerState = ConnectionManagerStates.Wait;

            if (WatchdogEnabled)
            {
                StartWatchDog();
            }

            InvokeEvent(ConnectionFound);
        }

        protected void ConnectionTimeoutEvent()
        {
            InvokeEvent(ConnectionTimeout);

            if (WatchdogEnabled)
            {
                StopWatchDog();

                if (PortScanEnabled)
                {
                    StartScan();
                }
                else
                {
                    StartConnect();
                }
            }
        }

        protected void InvokeEvent<TEventHandlerArguments>(EventHandler<TEventHandlerArguments> eventHandler,
            TEventHandlerArguments eventHandlerArguments) where TEventHandlerArguments : EventArgs
        {
            try
            {
                if (eventHandler == null) return;
                if (ControlToInvokeOn != null && ControlToInvokeOn.IsDisposed) return;
                if (ControlToInvokeOn != null && ControlToInvokeOn.InvokeRequired)
                {
                    //Asynchronously call on UI thread
                    ControlToInvokeOn.BeginInvoke((MethodInvoker) (() => eventHandler(this, eventHandlerArguments)));
                    Thread.Yield();
                }
                else
                {
                    //Directly call
                    eventHandler(this, eventHandlerArguments);
                }
            }
            catch (Exception)
            {
            }
        }

        protected void Log(int level, string logMessage)
        {
            var args = new ConnectionManagerProgressEventArgs {Level = level, Description = logMessage};
            InvokeEvent(Progress, args);
        }

        protected virtual void OnResponseCommandId(ReceivedCommand arguments)
        {
            // Do nothing. 
        }

        private void WorkerThreadDoWork(object sender, DoWorkEventArgs e)
        {
            if (Thread.CurrentThread.Name == null) Thread.CurrentThread.Name = "SerialConnectionManager";

            while (ConnectionManagerState != ConnectionManagerStates.Stop)
            {
                // Check if thread is being canceled
                var worker = sender as BackgroundWorker;
                if (worker != null && worker.CancellationPending)
                {
                    break;
                }

                // Switch between waiting, device scanning and watchdog 
                switch (ConnectionManagerState)
                {
                    case ConnectionManagerStates.Scan:
                        DoWorkScan();
                        break;
                    case ConnectionManagerStates.Connect:
                        DoWorkConnect();
                        break;
                    case ConnectionManagerStates.Watchdog:
                        DoWorkWatchdog();
                        break;
                }

                Thread.Sleep(1000);
            }
        }

        /// <summary>
        ///  Check if Arduino is available
        /// </summary>
        /// <param name="timeOut">timout for waiting on response</param>
        /// <returns>Result. True if succesfull</returns>
        public bool ArduinoAvailable(int timeOut)
        {
            var challengeCommand = new SendCommand(_challengeCommandId, _responseCommandId, timeOut);
            var responseCommand = CmdMessenger.SendCommand(challengeCommand,SendQueue.InFrontQueue,ReceiveQueue.Default,UseQueue.BypassQueue);
            return responseCommand.Ok;
        }

        public bool ArduinoAvailable(int timeOut, int tries)
        {
            for (var i = 1; i <= tries; i++)
            {
                Log(3, "Polling Arduino, try # " + i);
                if (ArduinoAvailable(timeOut)) return true;
            }
            return false;
        }

        protected abstract void DoWorkScan();

        protected abstract void DoWorkConnect();

        protected virtual void DoWorkWatchdog()
        {
            var lastLineTimeStamp = CmdMessenger.LastReceivedCommandTimeStamp;
            var currentTimeStamp = TimeUtils.Millis;

            // If timeout has not elapsed, wait till next watch time
            if (currentTimeStamp < _nextTimeOutCheck) return;

            // if a command has been received recently, set next check time
            if (lastLineTimeStamp > _lastCheckTime)
            {
                Log(3, "Successful watchdog response");
                _lastCheckTime = currentTimeStamp;
                _nextTimeOutCheck = _lastCheckTime + WatchdogTimeout;
                _watchdogTries = 0;
                return;
            }

            // Apparently, other side has not reacted in time
            // If too many tries, notify and stop
            if (_watchdogTries >= MaxWatchdogTries)
            {
                Log(3, "No watchdog response after final try");
                _watchdogTries = 0;
                ConnectionManagerState = ConnectionManagerStates.Wait;
                ConnectionTimeoutEvent();
                return;
            }

            // We'll try another time
            // We queue the command in order to not be intrusive, but put it in front to get a quick answer
            CmdMessenger.SendCommand(new SendCommand(_challengeCommandId), SendQueue.InFrontQueue, ReceiveQueue.Default);
            _watchdogTries++;

            _lastCheckTime = currentTimeStamp;
            _nextTimeOutCheck = _lastCheckTime + WatchdogRetryTimeout;
            Log(3, "No watchdog response, performing try #" + _watchdogTries);

            Thread.Sleep(100);
        }

        /// <summary>
        /// Disconnect from Arduino
        /// </summary>
        /// <returns>true if sucessfully disconnected</returns>
        public bool Disconnect()
        {
            if (Connected)
            {
                Connected = false;
                return CmdMessenger.Disconnect();
            }

            return true;
        }

        /// <summary>
        /// Start watchdog. Will check if connection gets interrupted
        /// </summary>
        protected virtual void StartWatchDog()
        {
            Log(1, "Starting Watchdog");
            _lastCheckTime = TimeUtils.Millis;
            _nextTimeOutCheck = _lastCheckTime + WatchdogTimeout;
            _watchdogTries = 0;
            ConnectionManagerState = ConnectionManagerStates.Watchdog;
        }

        /// <summary>
        /// Stop watchdog.
        /// </summary>
        protected virtual void StopWatchDog()
        {
            Log(1, "Stopping Watchdog");
            ConnectionManagerState = ConnectionManagerStates.Wait;
        }

        /// <summary>
        /// Stop scanning for devices
        /// </summary>
        public void StopScan()
        {
            Log(1, "Stopping device scan");
            ConnectionManagerState = ConnectionManagerStates.Wait;
        }

        /// <summary>
        /// Start scanning for devices
        /// </summary>
        public void StartScan()
        {
            Log(1, "Starting device scan");
            ConnectionManagerState = ConnectionManagerStates.Scan;
        }

        /// <summary>
        /// Start scanning for devices
        /// </summary>
        public void StartConnect()
        {
            Log(1, "Starting connecting to device");
            ConnectionManagerState = ConnectionManagerStates.Connect;
        }

        // Dispose 
        public void Dispose()
        {
            Dispose(true);
        }

        // Dispose
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopConnectionManager();
            }
        }
    }
}



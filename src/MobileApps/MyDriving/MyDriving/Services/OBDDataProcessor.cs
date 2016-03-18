﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.

using MyDriving.AzureClient;
using MyDriving.DataObjects;
using MyDriving.DataStore.Abstractions;
using MyDriving.Interfaces;
using MyDriving.Utils;
using Newtonsoft.Json;
using Plugin.Connectivity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MvvmHelpers;

namespace MyDriving.Services
{
    public class OBDDataProcessor
    {
        static OBDDataProcessor obdDataProcessor;

        //IOT Hub state
        IHubIOT iotHub;
        bool isConnectedToOBD;
        bool isInitialized = false;
        bool isPollingOBDDevice;
        bool isSendingBufferData;
        Timer obdConnectionTimer;

        //OBD Device state
        IOBDDevice obdDevice;
        TimeSpan obdEllapsedTime;
        object sendDataLock = new object();
        IStoreManager storeManager;

        private OBDDataProcessor()
        {
        }

        public bool IsOBDDeviceSimulated { get; set; }

        public static OBDDataProcessor GetProcessor()
        {
            if (obdDataProcessor == null)
            {
                obdDataProcessor = new OBDDataProcessor();
            }

            return obdDataProcessor;
        }

        //Init must be called each time to connect and reconnect to the OBD device
        public async Task Initialize(IStoreManager storeManager)
        {
            //Ensure that initialization is only performed once
            if (!isInitialized)
            {
                isInitialized = true;
                this.storeManager = storeManager;

                //Get platform specific implemenation of IOTHub and IOBDDevice
                iotHub = ServiceLocator.Instance.Resolve<IHubIOT>();
                obdDevice = ServiceLocator.Instance.Resolve<IOBDDevice>();

                //Start listening for connectivity change event so that we know if connection is restablished\dropped when pushing data to the IOT Hub
                CrossConnectivity.Current.ConnectivityChanged += Current_ConnectivityChanged;

                //Provision the device with the IOT Hub
                var connectionStr = await DeviceProvisionHandler.GetHandler().ProvisionDevice();
                iotHub.Initialize(connectionStr);

                //Check right away if there is any trip data left in the buffer that needs to be sent to the IOT Hub - run this thread in the background
                SendBufferedDataToIOTHub();
            }
        }

        public async Task SendTripPointToIOTHub(string tripId, string userId, TripPoint tripDataPoint)
        {
            //Note: Each individual trip point is being serialized separately so that it can be sent over as an individual message
            //This is the expected format by the IOT Hub\ML
            var settings = new JsonSerializerSettings();
            settings.ContractResolver = new CustomContractResolver();
            var tripDataPointBlob = JsonConvert.SerializeObject(tripDataPoint, settings);

            var tripBlob = JsonConvert.SerializeObject(
                new
                {
                    TripId = tripId,
                    UserId = userId
                });

            tripBlob = tripBlob.TrimEnd('}');
            string packagedBlob = String.Format("{0},\"TripDataPoint\":{1}}}", tripBlob, tripDataPointBlob);

            if (!CrossConnectivity.Current.IsConnected)
            {
                //If there is no network connection, save in buffer and try again
                Logger.Instance.WriteLine("Unable to push data to IOT Hub - no network connection.");
                await AddTripPointToBuffer(packagedBlob);
                return;
            }

            try
            {
                await iotHub.SendEvent(packagedBlob);
            }
            catch (Exception e)
            {
                //An exception will be thrown if the data isn't received by the IOT Hub; store data in buffer and try again
                Logger.Instance.WriteLine("Unable to send data to IOT Hub: " + e.Message);
                AddTripPointToBuffer(packagedBlob);
            }
        }

        private async Task AddTripPointToBuffer(string tripDataPointBlob)
        {
            IOTHubData iotHubData = new IOTHubData();
            iotHubData.Blob = tripDataPointBlob;

            await storeManager.IOTHubStore.InsertAsync(iotHubData);

            //Try to sending buffered data to the IOT Hub in the background
            SendBufferedDataToIOTHub();
        }

        /*
        This task loops through the buffered data and attempts to resend it to the IOT Hub until the entire buffer is empty.
        There are 3 places in this app where this task gets kicked off:
            (1) The first attempt that we make to send a trip point to the IOT Hub, if this fails, we add the trip point to the buffer and immediately call this task to retry
                sending buffered data to the IOT Hub. 
            (2) When network connectivity changes from being disconnected to connected, we kick this task off since it is highly likely that there may be data in the buffer
                since data cannot be sent to the IOT Hub when there is no connection.
            (3) When we first launch the app we kick this task off to see if there is any data remaining in the buffer from previous times that the app may have been run.
        */

        private async Task SendBufferedDataToIOTHub()
        {
            //Make sure that this thread can't be kicked off concurrently
            lock (sendDataLock)
            {
                if (isSendingBufferData)
                {
                    return;
                }
                else
                {
                    isSendingBufferData = true;
                }
            }

            var iotHubDataBlobs = new List<IOTHubData>(await storeManager.IOTHubStore.GetItemsAsync());

            while (CrossConnectivity.Current.IsConnected && iotHubDataBlobs.Count() > 0)
            {
                try
                {
                    //Once all the data is pushed to the IOT Hub, delete it from the buffer
                    //Note: This could still be pushing a bunch of data at once, but running in the background should make the performance impact of this unnoticable
                    await iotHub.SendEvent(iotHubDataBlobs[0].Blob);
                    await storeManager.IOTHubStore.RemoveAsync(iotHubDataBlobs[0]);
                }
                catch (Exception e)
                {
                    //An exception will be thrown if the data isn't received by the IOT Hub - wait a few seconds and try again
                    Logger.Instance.WriteLine("Unable to send buffered data to IOT Hub: " + e.Message);
                    await Task.Delay(3000);
                }
                finally
                {
                    iotHubDataBlobs = new List<IOTHubData>(await storeManager.IOTHubStore.GetItemsAsync());
                }
            }

            lock (sendDataLock)
            {
                isSendingBufferData = false;
            }
        }

        private void Current_ConnectivityChanged(object sender,
            Plugin.Connectivity.Abstractions.ConnectivityChangedEventArgs e)
        {
            if (e.IsConnected)
            {
                //If connection is re-established, then kick of background thread to push buffered data to IOT Hub
                SendBufferedDataToIOTHub();
            }
        }

        public async Task<Dictionary<String, String>> ReadOBDData()
        {
            Dictionary<String, String> obdData = null;
            if (isConnectedToOBD)
            {
                //Null is returned if connection to the OBD device is dropped
                obdData = obdDevice.ReadData();

                if (obdData == null)
                {
                    //Invoke in background so that caller isn't blocked while trying to connect to OBD device
                    isConnectedToOBD = false;
                    ConnectToOBDDevice(false);
                }
            }

            return obdData;
        }

        public async Task DisconnectFromOBDDevice()
        {
            //Stop polling the device in case we're still trying to connect to it
            StopPollingOBDDevice();

            //Disconnect to the device in the case that we did successfull connect to it
            if (!IsOBDDeviceSimulated)
                await obdDevice.Disconnect();

            isConnectedToOBD = false;
        }

        public async Task ConnectToOBDDevice(bool showConfirmDialog)
        {
            IsOBDDeviceSimulated = false;
            if (showConfirmDialog)
            {
                //Prompts user with dialog to retry if connection to OBD device fails
                isConnectedToOBD = await ConnectToOBDDeviceWithConfirmation();
            }
            else
            {
                //Silently attempts to connect to the OBD device
                StartPollingOBDDevice();
            }
        }

        private async Task<bool> ConnectToOBDDeviceWithConfirmation()
        {
            var isConnected = false;
            var progress = Acr.UserDialogs.UserDialogs.Instance.Loading("Connecting to OBD Device...",
                maskType: Acr.UserDialogs.MaskType.Clear);

            if (obdDevice == null)
            {
                obdDevice = ServiceLocator.Instance.Resolve<IOBDDevice>();
            }

            try
            {
                isConnected = await Task.Run(async () => await obdDevice.Initialize()).WithTimeout(5000);
            }
            catch (Exception ex)
            {
                Logger.Instance.WriteLine(ex.ToString());
            }
            finally
            {
                progress.Dispose();
            }

            if (!isConnected)
            {
                var result =
                    await
                        Acr.UserDialogs.UserDialogs.Instance.ConfirmAsync(
                            "Unable to connect to OBD device.  Would you like to attempt to connect to the OBD device again?",
                            "OBD Connection", "Retry", "Use Simulator");

                if (result)
                {
                    //Attempt to connect to OBD device again
                    isConnected = await ConnectToOBDDeviceWithConfirmation();
                }
                else
                {
                    //Use the OBD simulator
                    try
                    {
                        isConnected = await Task.Run(async () => await obdDevice.Initialize(true)).WithTimeout(5000);
                    }
                    catch (Exception ex)
                    {
                        IsOBDDeviceSimulated = obdDevice.IsSimulated;
                    }
                }
            }

            return isConnected;
        }

        private void StartPollingOBDDevice()
        {
            if (!isConnectedToOBD && !isPollingOBDDevice)
            {
                isPollingOBDDevice = true;
                obdEllapsedTime = new TimeSpan(0, 0, 0);
                obdConnectionTimer = new Timer(OnTick, 0, 1000, 1000);
            }
        }

        private void StopPollingOBDDevice()
        {
            if (isPollingOBDDevice)
            {
                obdConnectionTimer.Cancel();
                obdConnectionTimer.Dispose();
                isPollingOBDDevice = false;
                obdEllapsedTime = new TimeSpan(0, 0, 0);
            }
        }

        private async Task TryToConnectToOBDDevice()
        {
            try
            {
                if (obdDevice == null)
                    return;

                isConnectedToOBD = await Task.Run(async () => await obdDevice.Initialize()).WithTimeout(5000);
                if (isConnectedToOBD)
                {
                    StopPollingOBDDevice();
                }
            }
            catch (Exception)
            {
            }
        }

        private async void OnTick(object args)
        {
            obdEllapsedTime = obdEllapsedTime.Add(new TimeSpan(0, 0, 1));

            if (obdEllapsedTime.TotalMinutes < 5 && obdEllapsedTime.TotalSeconds%10 == 0)
            {
                //Try to connect every 10 seconds if the OBD device disconnected time is 5 mins or less
                await TryToConnectToOBDDevice();
            }
            else if (obdEllapsedTime.TotalMinutes < 30 && obdEllapsedTime.TotalMinutes%5 == 0)
            {
                //Try to connect every 5 minutes if the OBD device disconnected time is 30 mins or less
                await TryToConnectToOBDDevice();
            }
            else if (obdEllapsedTime.TotalHours < 3 && obdEllapsedTime.TotalMinutes%30 == 0)
            {
                //Otherwise, try to connect every 30 minutes
                await TryToConnectToOBDDevice();
            }
            else if (obdEllapsedTime.TotalHours >= 3)
            {
                //Give up after 3 hours
                StopPollingOBDDevice();
            }
        }
    }
}
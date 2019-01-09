// Copyright (c) Microsoft. All rights reserved.
namespace LeafDevice.Details
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using global::LeafDevice.Details;
    using Microsoft.Azure.Devices;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Common;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Azure.EventHubs;
    using DeviceClientTransportType = Microsoft.Azure.Devices.Client.TransportType;
    using EventHubClientTransportType = Microsoft.Azure.EventHubs.TransportType;
    using IotHubConnectionStringBuilder = Microsoft.Azure.Devices.IotHubConnectionStringBuilder;
    using Message = Microsoft.Azure.Devices.Client.Message;
    using ServiceClientTransportType = Microsoft.Azure.Devices.TransportType;

    public struct DeviceCertificate
    {
        public string certificateFilePath;
        public string certificateKeyFilePath;
    }

    public class Details
    {
        readonly string iothubConnectionString;
        readonly string eventhubCompatibleEndpointWithEntityPath;
        readonly string deviceId;
        readonly string trustedCACertificateFileName;
        readonly string edgeHostName;
        readonly ServiceClientTransportType serviceClientTransportType;
        readonly EventHubClientTransportType eventHubClientTransportType;
        readonly ITransportSettings[] deviceTransportSettings;
        Option<X509Certificate2> clientCertificate = Option.None<X509Certificate2>();
        Option<IEnumerable<X509Certificate2>> clientCertificateChain = Option.None<IEnumerable<X509Certificate2>>();
        Option<List<string>> thumbprints = Option.None<List<string>>();
        AuthenticationType authType = AuthenticationType.None;
        DeviceContext context;

        protected Details(
            string iothubConnectionString,
            string eventhubCompatibleEndpointWithEntityPath,
            string deviceId,
            string trustedCACertificateFileName,
            string edgeHostName,
            bool useWebSockets,
            Option<DeviceCertificate> clientCertificatePaths,
            Option<IList<string>> thumbprintCertificatePaths)
        {
            this.iothubConnectionString = iothubConnectionString;
            this.eventhubCompatibleEndpointWithEntityPath = eventhubCompatibleEndpointWithEntityPath;
            this.deviceId = deviceId;
            this.trustedCACertificateFileName = trustedCACertificateFileName;
            this.edgeHostName = edgeHostName;
            this.authType = clientCertificatePaths.Map(
                clientCred =>
                {
                    (X509Certificate2 clientCert, IEnumerable<X509Certificate2> clientCertChain) =
                        CertificateHelper.GetServerCertificateAndChainFromFile(clientCred.certificateFilePath, clientCred.certificateKeyFilePath);
                    this.clientCertificate = Option.Some(clientCert);

                    var authType = AuthenticationType.CertificateAuthority;
                    this.thumbprints = thumbprintCertificatePaths.Map(
                        certificates =>
                        {
                            if (certificates.Count != 2)
                            {
                                throw new ArgumentException($"Exactly two client thumprint certificates expected");
                            }
                            if (string.IsNullOrWhiteSpace(certificates[0]) || !File.Exists(certificates[0]))
                            {
                                throw new ArgumentException($"'{certificates[0]}' is not a path to a thumbprint certificate file");
                            }
                            if (string.IsNullOrWhiteSpace(certificates[1]) || !File.Exists(certificates[1]))
                            {
                                throw new ArgumentException($"'{certificates[1]}' is not a path to a thumbprint certificate file");
                            }

                            authType = AuthenticationType.SelfSigned;
                            var rawCerts = new List<string>();
                            foreach (string dc in certificates)
                            {
                                string rawCert;
                                using (var sr = new StreamReader(dc))
                                {
                                    rawCert = sr.ReadToEnd();
                                }
                                rawCerts.Add(rawCert);
                            }
                            var certs = CertificateHelper.GetCertificatesFromPem(rawCerts);
                            var thumbprints = new List<string>();
                            foreach (X509Certificate2 cert in certs)
                            {
                                thumbprints.Add(cert.Thumbprint.ToUpper());
                            }
                            return thumbprints;
                        });
                    if (authType == AuthenticationType.CertificateAuthority)
                    {
                        this.clientCertificateChain = Option.Some(clientCertChain);
                    }
                    return authType;
                }).GetOrElse(AuthenticationType.Sas);

            if (useWebSockets)
            {
                this.serviceClientTransportType = ServiceClientTransportType.Amqp_WebSocket_Only;
                this.eventHubClientTransportType = EventHubClientTransportType.AmqpWebSockets;
                this.deviceTransportSettings = new ITransportSettings[] { new MqttTransportSettings(DeviceClientTransportType.Mqtt_WebSocket_Only) };
            }
            else
            {
                this.serviceClientTransportType = ServiceClientTransportType.Amqp;
                this.eventHubClientTransportType = EventHubClientTransportType.Amqp;
                this.deviceTransportSettings = new ITransportSettings[] { new MqttTransportSettings(DeviceClientTransportType.Mqtt_Tcp_Only) };
            }
        }

        public static void InstallCerts(StoreName name, StoreLocation location, IEnumerable<X509Certificate2> certs)
        {
            List<X509Certificate2> certsList = certs.ToList();
            using (var store = new X509Store(name, location))
            {
                store.Open(OpenFlags.ReadWrite);
                foreach (X509Certificate2 cert in certsList)
                {
                    store.Add(cert);
                }
            }
        }

        static void InstallTrustedCACerts(IEnumerable<X509Certificate2> trustedCertificates)
        {
            // Since Windows will pop up security warning when add certificate to current user store location;
            StoreName name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StoreName.CertificateAuthority : StoreName.Root;
            List<X509Certificate2> certsList = trustedCertificates.ToList();
            using (var store = new X509Store(name, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                foreach (X509Certificate2 cert in certsList)
                {
                    store.Add(cert);
                }
            }
        }

        protected Task InitializeTrustedCertsAsync()
        {
            if (!string.IsNullOrEmpty(this.trustedCACertificateFileName))
            {
                // Since Windows will pop up security warning when add certificate to current user store location;
                // Therefore we will use CustomCertificateValidator instead.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // This will hook up callback on device transport settings to validate with given certificate
                    CustomCertificateValidator.Create(new List<X509Certificate2> { this.GetTrustedCertificate() }, this.deviceTransportSettings);
                }
                else
                {
                    InstallTrustedCACerts(new List<X509Certificate2>() { this.GetTrustedCertificate() });
                }
            }

            return clientCertificateChain.Map(
                certs =>
                {
                    InstallTrustedCACerts(certs);
                    return Task.CompletedTask;
                }).GetOrElse(Task.CompletedTask);
        }

        protected async Task ConnectToEdgeAndSendData()
        {
            IotHubConnectionStringBuilder builder = IotHubConnectionStringBuilder.Create(this.iothubConnectionString);
            DeviceClient deviceClient;
            if (this.authType == AuthenticationType.Sas)
            {
                string leafDeviceConnectionString = $"HostName={builder.HostName};DeviceId={this.deviceId};SharedAccessKey={this.context.Device.Authentication.SymmetricKey.PrimaryKey};GatewayHostName={this.edgeHostName}";
                deviceClient = DeviceClient.CreateFromConnectionString(leafDeviceConnectionString, this.deviceTransportSettings);
            }
            else
            {
                var auth = new DeviceAuthenticationWithX509Certificate(this.deviceId, this.clientCertificate.Expect(() => new InvalidOperationException("Missing client certificate")));
                deviceClient = DeviceClient.Create(builder.HostName, this.edgeHostName, auth, this.deviceTransportSettings);
            }

            this.context.DeviceClientInstance = Option.Some(deviceClient);
            Console.WriteLine("Leaf Device client created.");

            var message = new Message(Encoding.ASCII.GetBytes($"Message from Leaf Device. Msg GUID: {this.context.MessageGuid}"));
            Console.WriteLine($"Trying to send the message to '{this.edgeHostName}'");

            await deviceClient.SendEventAsync(message);
            Console.WriteLine("Message Sent.");
            await deviceClient.SetMethodHandlerAsync("DirectMethod", DirectMethod, null).ConfigureAwait(false);
            Console.WriteLine("Direct method callback is set.");
        }

        protected async Task GetOrCreateDeviceIdentityAsync()
        {
            IotHubConnectionStringBuilder builder = IotHubConnectionStringBuilder.Create(this.iothubConnectionString);
            RegistryManager rm = RegistryManager.CreateFromConnectionString(builder.ToString());

            Device device = await rm.GetDeviceAsync(this.deviceId);

            if (device != null)
            {
                Console.WriteLine($"Device '{device.Id}' already registered on IoT hub '{builder.HostName}'");

                if (this.authType == AuthenticationType.SelfSigned)
                {
                    // always update the thumbprints before attempting to run any tests to ensure consistency
                    device.Authentication.X509Thumbprint = this.thumbprints.Map(
                        thList =>
                        {
                            return new X509Thumbprint() { PrimaryThumbprint = thList[0], SecondaryThumbprint = thList[1] };
                        }).GetOrElse(new X509Thumbprint());
                    await rm.UpdateDeviceAsync(device);
                }

                this.context = new DeviceContext
                {
                    Device = device,
                    IotHubConnectionString = this.iothubConnectionString,
                    RegistryManager = rm,
                    RemoveDevice = false,
                    MessageGuid = Guid.NewGuid().ToString()
                };
            }
            else
            {
                await this.CreateDeviceIdentityAsync(rm);
            }
        }

        protected async Task VerifyDataOnIoTHub()
        {
            var builder = new EventHubsConnectionStringBuilder(this.eventhubCompatibleEndpointWithEntityPath);
            builder.TransportType = this.eventHubClientTransportType;

            Console.WriteLine($"Receiving events from device '{this.context.Device.Id}' on Event Hub '{builder.EntityPath}'");

            EventHubClient eventHubClient =
                EventHubClient.CreateFromConnectionString(builder.ToString());

            PartitionReceiver eventHubReceiver = eventHubClient.CreateReceiver(
                "$Default",
                EventHubPartitionKeyResolver.ResolveToPartition(
                    this.context.Device.Id,
                    (await eventHubClient.GetRuntimeInformationAsync()).PartitionCount),
                EventPosition.FromEnqueuedTime(DateTime.Now.AddMinutes(-5)));

            var result = new TaskCompletionSource<bool>();
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                using (cts.Token.Register(() => result.TrySetCanceled()))
                {
                    eventHubReceiver.SetReceiveHandler(
                        new PartitionReceiveHandler(
                            eventData =>
                            {
                                eventData.SystemProperties.TryGetValue("iothub-connection-device-id", out object devId);

                                if (devId != null && devId.ToString().Equals(this.context.Device.Id)
                                                  && Encoding.UTF8.GetString(eventData.Body).Contains(this.context.MessageGuid))
                                {
                                    result.TrySetResult(true);
                                    return true;
                                }

                                return false;
                            }));

                    await result.Task;
                }
            }

            await eventHubReceiver.CloseAsync();
            await eventHubClient.CloseAsync();
        }

        protected async Task VerifyDirectMethod()
        {
            // User Service SDK to invoke Direct Method on the device.
            ServiceClient serviceClient =
                ServiceClient.CreateFromConnectionString(this.context.IotHubConnectionString, this.serviceClientTransportType);

            // Call a direct method
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300)))
            {
                CloudToDeviceMethod cloudToDeviceMethod = new CloudToDeviceMethod("DirectMethod").SetPayloadJson("{\"TestKey\" : \"TestValue\"}");

                CloudToDeviceMethodResult result = await serviceClient.InvokeDeviceMethodAsync(
                    this.context.Device.Id,
                    cloudToDeviceMethod,
                    cts.Token);

                if (result.Status != 200)
                {
                    throw new Exception("Could not invoke Direct Method on Device.");
                }
                else if (!result.GetPayloadAsJson().Equals("{\"TestKey\":\"TestValue\"}"))
                {
                    throw new Exception($"Payload doesn't match with Sent Payload. Received payload: {result.GetPayloadAsJson()}. Expected: {{\"TestKey\":\"TestValue\"}}");
                }
            }
        }

        protected void KeepDeviceIdentity()
        {
            if (this.context != null)
            {
                this.context.RemoveDevice = false;
            }
        }

        protected Task MaybeDeleteDeviceIdentity()
        {
            if (this.context != null)
            {
                Device device = this.context.Device;
                bool remove = this.context.RemoveDevice;
                this.context.Device = null;

                if (remove)
                {
                    return this.context.RegistryManager.RemoveDeviceAsync(device);
                }
            }

            return Task.CompletedTask;
        }

        static Task<MethodResponse> DirectMethod(MethodRequest methodRequest, object userContext)
        {
            Console.WriteLine($"Leaf device received direct method call...Payload Received: {methodRequest.DataAsJson}");
            return Task.FromResult(new MethodResponse(methodRequest.Data, (int)HttpStatusCode.OK));
        }

        X509Certificate2 GetTrustedCertificate() => new X509Certificate2(X509Certificate.CreateFromCertFile(this.trustedCACertificateFileName));

        async Task CreateDeviceIdentity(RegistryManager rm)
        {
            var device = new Device(this.deviceId)
            {
                Authentication = new AuthenticationMechanism() { Type = AuthenticationType.Sas },
                Capabilities = new DeviceCapabilities() { IotEdge = false }
            };

            IotHubConnectionStringBuilder builder = IotHubConnectionStringBuilder.Create(this.iothubConnectionString);
            Console.WriteLine($"Registering device '{device.Id}' on IoT hub '{builder.HostName}'");

            device = await rm.AddDeviceAsync(device);

            this.context = new DeviceContext
            {
                Device = device,
                DeviceClientInstance = Option.None<DeviceClient>(),
                IotHubConnectionString = this.iothubConnectionString,
                RegistryManager = rm,
                RemoveDevice = true,
                MessageGuid = Guid.NewGuid().ToString()
            };
        }
    }

    public class DeviceContext
    {
        public Device Device { get; set; }

        public Option<DeviceClient> DeviceClientInstance { get; set; }

        public string IotHubConnectionString { get; set; }

        public RegistryManager RegistryManager { get; set; }

        public bool RemoveDevice { get; set; }

        public string MessageGuid { get; set; } // used to identify exactly which message got sent.
    }
}

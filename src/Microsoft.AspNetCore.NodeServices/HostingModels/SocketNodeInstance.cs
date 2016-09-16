using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.NodeServices.HostingModels.PhysicalConnections;
using Microsoft.AspNetCore.NodeServices.HostingModels.VirtualConnections;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Microsoft.AspNetCore.NodeServices.HostingModels
{
    /// <summary>
    /// A specialisation of the OutOfProcessNodeInstance base class that uses a lightweight binary streaming protocol
    /// to perform RPC invocations. The physical transport is Named Pipes on Windows, or Domain Sockets on Linux/Mac.
    /// For details on the binary streaming protocol, see
    /// Microsoft.AspNetCore.NodeServices.HostingModels.VirtualConnections.VirtualConnectionClient.
    /// The advantage versus using HTTP for RPC is that this is faster (not surprisingly - there's much less overhead
    /// because we don't need most of the functionality of HTTP.
    ///
    /// The address of the pipe/socket is selected randomly here on the .NET side and sent to the child process as a
    /// command-line argument (the address space is wide enough that there's no real risk of a clash, unlike when
    /// selecting TCP port numbers).
    /// </summary>
    /// <seealso cref="Microsoft.AspNetCore.NodeServices.HostingModels.OutOfProcessNodeInstance" />
    internal class SocketNodeInstance : OutOfProcessNodeInstance
    {
        private readonly static JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            TypeNameHandling = TypeNameHandling.None
        };

        private readonly SemaphoreSlim _connectionCreationSemaphore = new SemaphoreSlim(1);
        private bool _connectionHasFailed;
        private StreamConnection _physicalConnection;
        private string _socketAddress;
        private VirtualConnectionClient _virtualConnectionClient;

        public SocketNodeInstance(string projectPath, string[] watchFileExtensions, string socketAddress,
            ILogger nodeInstanceOutputLogger, IDictionary<string, string> environmentVars,
            int invocationTimeoutMilliseconds, bool launchWithDebugging, int debuggingPort)
        : base(
                EmbeddedResourceReader.Read(
                    typeof(SocketNodeInstance),
                    "/Content/Node/entrypoint-socket.js"),
                projectPath,
                watchFileExtensions,
                MakeNewCommandLineOptions(socketAddress),
                nodeInstanceOutputLogger,
                environmentVars,
                invocationTimeoutMilliseconds,
                launchWithDebugging,
                debuggingPort)
        {
            _socketAddress = socketAddress;
        }

        protected override async Task<T> InvokeExportAsync<T>(NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
        {
            if (_connectionHasFailed)
            {
                // This special exception type forces NodeServicesImpl to restart the Node instance
                throw new NodeInvocationException(
                    "The SocketNodeInstance socket connection failed. See logs to identify the reason.",
                    null,
                    nodeInstanceUnavailable: true);
            }

            if (_virtualConnectionClient == null)
            {
                // Although we could pass the cancellationToken into EnsureVirtualConnectionClientCreated and
                // have it signal cancellations upstream, that would be a bad thing to do, because all callers
                // wait for the same connection task. There's no reason why the first caller should have the
                // special ability to cancel the connection process in a way that would affect subsequent
                // callers. So, each caller just independently stops awaiting connection if that call is cancelled.
                await EnsureVirtualConnectionClientCreated().OrThrowOnCancellation(cancellationToken);
            }

            // For each invocation, we open a new virtual connection. This gives an API equivalent to opening a new
            // physical connection to the child process, but without the overhead of doing so, because it's really
            // just multiplexed into the existing physical connection stream.
            bool shouldDisposeVirtualConnection = true;
            Stream virtualConnection = null;
            try
            {
                virtualConnection = _virtualConnectionClient.OpenVirtualConnection();

                // Send request
                await WriteJsonLineAsync(virtualConnection, invocationInfo, cancellationToken);

                // Determine what kind of response format is expected
                if (typeof(T) == typeof(Stream))
                {
                    // Pass through streamed binary response
                    // It is up to the consumer to dispose this stream, so don't do so here
                    shouldDisposeVirtualConnection = false;
                    return (T)(object)virtualConnection;
                }
                else
                {
                    // Parse and return non-streamed JSON response
                    var response = await ReadJsonAsync<RpcJsonResponse<T>>(virtualConnection, cancellationToken);
                    if (response.ErrorMessage != null)
                    {
                        throw new NodeInvocationException(response.ErrorMessage, response.ErrorDetails);
                    }

                    return response.Result;
                }
            }
            finally
            {
                if (shouldDisposeVirtualConnection)
                {
                    virtualConnection.Dispose();
                }
            }
        }

        private async Task EnsureVirtualConnectionClientCreated()
        {
            // Asynchronous equivalent to a 'lock(...) { ... }'
            await _connectionCreationSemaphore.WaitAsync();
            try
            {
                if (_virtualConnectionClient == null)
                {
                    _physicalConnection = StreamConnection.Create();

                    var connection = await _physicalConnection.Open(_socketAddress);
                    _virtualConnectionClient = new VirtualConnectionClient(connection);
                    _virtualConnectionClient.OnError += (ex) =>
                    {
                        // This callback is fired only if there's a protocol-level failure (e.g., child process disconnected
                        // unexpectedly). It does *not* fire when RPC calls return errors. Since there's been a protocol-level
                        // failure, this Node instance is no longer usable and should be discarded.
                        _connectionHasFailed = true;

                        OutputLogger.LogError(0, ex, ex.Message);
                    };
                }
            }
            finally
            {
                _connectionCreationSemaphore.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_virtualConnectionClient != null)
                {
                    _virtualConnectionClient.Dispose();
                    _virtualConnectionClient = null;
                }

                if (_physicalConnection != null)
                {
                    _physicalConnection.Dispose();
                    _physicalConnection = null;
                }
            }

            base.Dispose(disposing);
        }

        private static async Task WriteJsonLineAsync(Stream stream, object serializableObject, CancellationToken cancellationToken)
        {
            var json = JsonConvert.SerializeObject(serializableObject, jsonSerializerSettings);
            var bytes = Encoding.UTF8.GetBytes(json + '\n');
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        }

        private static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(await ReadAllBytesAsync(stream, cancellationToken));
            return JsonConvert.DeserializeObject<T>(json, jsonSerializerSettings);
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream input, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];

            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }

                return ms.ToArray();
            }
        }

        private static string MakeNewCommandLineOptions(string listenAddress)
        {
            return $"--listenAddress {listenAddress}";
        }

#pragma warning disable 649 // These properties are populated via JSON deserialization
        private class RpcJsonResponse<TResult>
        {
            public TResult Result { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorDetails { get; set; }
        }
#pragma warning restore 649
    }
}
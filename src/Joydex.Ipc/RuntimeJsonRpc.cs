using System.IO.Pipelines;
using Newtonsoft.Json;
using StreamJsonRpc;

namespace Joydex.Ipc;

internal static class RuntimeJsonRpc
{
    private const int TransportBufferBytes = 4096;

    public static (JsonRpc Rpc, BoundedMessageStream Stream) Create(Stream stream)
    {
        var boundedStream = new BoundedMessageStream(
            stream,
            Contracts.RuntimeProtocol.MaximumMessageBytes);
        var formatter = new JsonMessageFormatter();
        formatter.JsonSerializer.TypeNameHandling = TypeNameHandling.None;
        formatter.JsonSerializer.MetadataPropertyHandling = MetadataPropertyHandling.Ignore;
        formatter.JsonSerializer.MaxDepth = 64;

        var reader = PipeReader.Create(
            boundedStream,
            new StreamPipeReaderOptions(
                bufferSize: TransportBufferBytes,
                minimumReadSize: sizeof(int),
                leaveOpen: true));
        var writer = PipeWriter.Create(
            boundedStream,
            new StreamPipeWriterOptions(
                minimumBufferSize: TransportBufferBytes,
                leaveOpen: true));
        var handler = new LengthHeaderMessageHandler(writer, reader, formatter);
        var rpc = new JsonRpc(handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            ExceptionStrategy = ExceptionProcessing.CommonErrorData,
        };
        return (rpc, boundedStream);
    }
}

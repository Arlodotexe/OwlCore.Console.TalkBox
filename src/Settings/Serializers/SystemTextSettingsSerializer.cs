using System.Text.Json.Serialization;
using CommunityToolkit.Diagnostics;
using OwlCore.ComponentModel;

namespace OwlCore.Console.TalkBox.Settings;

/// <summary>
/// An <see cref="IAsyncSerializer{TSerialized}"/> and implementation for serializing and deserializing streams using System.Text.Json.
/// </summary>
public class SystemTextSettingsSerializer(JsonSerializerContext Context) : IAsyncSerializer<Stream>, ISerializer<Stream>
{
    /// <summary>
    /// A singleton instance for <see cref="SystemTextSettingsSerializer"/>.

    /// <inheritdoc />
    public async Task<Stream> SerializeAsync<T>(T data, CancellationToken? cancellationToken = null)
    {
        var stream = new MemoryStream();
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, data, typeof(T), context: Context, cancellationToken: cancellationToken ?? CancellationToken.None);
        return stream;
    }

    /// <inheritdoc />
    public async Task<Stream> SerializeAsync(Type inputType, object data, CancellationToken? cancellationToken = null)
    {
        var stream = new MemoryStream();
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, data, inputType, context: Context, cancellationToken: cancellationToken ?? CancellationToken.None);
        return stream;
    }

    /// <inheritdoc />
    public async Task<TResult> DeserializeAsync<TResult>(Stream serialized, CancellationToken? cancellationToken = null)
    {
        var result = await System.Text.Json.JsonSerializer.DeserializeAsync(serialized, typeof(TResult), Context);
        Guard.IsNotNull(result);
        return (TResult)result;
    }

    /// <inheritdoc />
    public async Task<object> DeserializeAsync(Type returnType, Stream serialized, CancellationToken? cancellationToken = null)
    {
        var result = await System.Text.Json.JsonSerializer.DeserializeAsync(serialized, returnType, Context);
        Guard.IsNotNull(result);
        return result;
    }

    /// <inheritdoc />
    public Stream Serialize<T>(T data)
    {
        var stream = new MemoryStream();
        System.Text.Json.JsonSerializer.SerializeAsync(stream, data, typeof(T), context: Context, cancellationToken: CancellationToken.None);
        return stream;
    }

    /// <inheritdoc />
    public Stream Serialize(Type type, object data)
    {
        var stream = new MemoryStream();
        System.Text.Json.JsonSerializer.SerializeAsync(stream, data, type, context: Context, cancellationToken: CancellationToken.None);
        return stream;
    }

    /// <inheritdoc />
    public TResult Deserialize<TResult>(Stream serialized)
    {
        var result = System.Text.Json.JsonSerializer.Deserialize(serialized, typeof(TResult), Context);
        Guard.IsNotNull(result);
        return (TResult)result;
    }

    /// <inheritdoc />
    public object Deserialize(Type type, Stream serialized)
    {
        var result = System.Text.Json.JsonSerializer.Deserialize(serialized, type, Context);
        Guard.IsNotNull(result);
        return result;
    }
}

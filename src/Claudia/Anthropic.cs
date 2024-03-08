﻿using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claudia;

// https://docs.anthropic.com/claude/reference/getting-started-with-the-api

public interface IMessages
{
    Task<MessagesResponse> CreateAsync(MessageRequest request, RequestOptions? overrideOptions = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<IMessageStreamEvent> CreateStreamAsync(MessageRequest request, RequestOptions? overrideOptions = null, CancellationToken cancellationToken = default);
}

public class RequestOptions
{
    public TimeSpan? Timeout { get; set; }

    public int? MaxRetries { get; set; }
}

public class Anthropic : IMessages, IDisposable
{
    internal static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    readonly HttpClient httpClient;
    readonly Random random = new Random();

    public string ApiKey { get; init; } = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Create a Message.
    /// Send a structured list of input messages with text and/or image content, and the model will generate the next message in the conversation.
    /// The Messages API can be used for for either single queries or stateless multi-turn conversations.
    /// </summary>
    public IMessages Messages => this;

    public Anthropic()
    : this(new HttpClientHandler(), true)
    {
    }

    public Anthropic(HttpMessageHandler handler)
        : this(handler, true)
    {
    }

    public Anthropic(HttpMessageHandler handler, bool disposeHandler)
    {
        this.httpClient = new HttpClient(handler, disposeHandler);
        this.httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // ignore use Timeout, control manually because requires override Timeout option per request.
    }

    async Task<MessagesResponse> IMessages.CreateAsync(MessageRequest request, RequestOptions? overrideOptions, CancellationToken cancellationToken)
    {
        request.Stream = null;
        var msg = await SendRequestAsync(request, overrideOptions, cancellationToken).ConfigureAwait(false);
        var result = await RequestWithCancelAsync(msg, cancellationToken, overrideOptions, false, static (x, ct) => x.Content.ReadFromJsonAsync<MessagesResponse>(DefaultJsonSerializerOptions, ct)).ConfigureAwait(false);
        return result!;
    }

    async IAsyncEnumerable<IMessageStreamEvent> IMessages.CreateStreamAsync(MessageRequest request, RequestOptions? overrideOptions, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Stream = true;
        var msg = await SendRequestAsync(request, overrideOptions, cancellationToken).ConfigureAwait(false);

        using var stream = await msg.Content.ReadAsStreamAsync().ConfigureAwait(false);

        var reader = new StreamMessageReader(stream);

        await foreach (var item in reader.ReadMessagesAsync(cancellationToken))
        {
            yield return item;
        }
    }

    async Task<HttpResponseMessage> SendRequestAsync(MessageRequest request, RequestOptions? overrideOptions, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, DefaultJsonSerializerOptions);

        var message = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.Messages);
        message.Headers.Add("x-api-key", ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        message.Headers.Add("Accept", "application/json");
        message.Content = new ByteArrayContent(bytes);

        // use ResponseHeadersRead to ignore buffering response.
        var msg = await RequestWithCancelAsync((httpClient, message), cancellationToken, overrideOptions, true, static (x, ct) => x.httpClient.SendAsync(x.message, HttpCompletionOption.ResponseHeadersRead, ct)).ConfigureAwait(false);
        var statusCode = (int)msg.StatusCode;

        switch (statusCode)
        {
            case 200:
                return msg;
            default:
                var shape = await RequestWithCancelAsync(msg, cancellationToken, overrideOptions, false, static (x, ct) => x.Content.ReadFromJsonAsync<ErrorResponseShape>(DefaultJsonSerializerOptions, ct)).ConfigureAwait(false);

                var error = shape!.ErrorResponse;
                var errorMsg = error.Message;
                var code = (ErrorCode)statusCode;
                if (code == ErrorCode.InvalidRequestError)
                {
                    errorMsg += ". Input: " + JsonSerializer.Serialize(request, DefaultJsonSerializerOptions);
                }
                throw new ClaudiaException((ErrorCode)statusCode, error.Type, errorMsg);
        }
    }

    async Task<TResult> RequestWithCancelAsync<TResult, TState>(TState state, CancellationToken cancellationToken, RequestOptions? overrideOptions, bool doRetry, Func<TState, CancellationToken, Task<TResult>> func)
    {
        var retriesRemaining = !doRetry ? 0 : overrideOptions?.MaxRetries ?? MaxRetries;
        var timeout = overrideOptions?.Timeout ?? Timeout;
    RETRY:
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(timeout);

            try
            {
                try
                {
                    return await func(state, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ex.Message, ex, cancellationToken);
                    }
                    else
                    {
                        throw new TimeoutException($"The request was canceled due to the configured Timeout of {Timeout.TotalSeconds} seconds elapsing.", ex);
                    }

                    throw;
                }
            }
            catch
            {
                if (retriesRemaining > 0)
                {
                    var sleep = CalculateDefaultRetryTimeoutMillis(random, retriesRemaining, MaxRetries);
                    await Task.Delay(TimeSpan.FromMilliseconds(sleep), cancellationToken).ConfigureAwait(false);
                    retriesRemaining--;
                    goto RETRY;
                }
                throw;
            }
        }
    }

    // same logic of official client
    static double CalculateDefaultRetryTimeoutMillis(Random random, int retriesRemaining, int maxRetries)
    {
        const double initialRetryDelay = 0.5;
        const double maxRetryDelay = 8.0;

        var numRetries = maxRetries - retriesRemaining;

        // Apply exponential backoff, but not more than the max.
        var sleepSeconds = Math.Min(initialRetryDelay * Math.Pow(2, numRetries), maxRetryDelay);

        // Apply some jitter, take up to at most 25 percent of the retry time.
        var jitter = 1 - random.NextDouble() * 0.25;

        return sleepSeconds * jitter * 1000;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    static class ApiEndpoints
    {
        public static readonly Uri Messages = new Uri("https://api.anthropic.com/v1/messages", UriKind.RelativeOrAbsolute);
    }
}

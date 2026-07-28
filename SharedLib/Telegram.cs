using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Web;

namespace WhalesSecret.ScriptApiLib.Samples.SharedLib;

/// <summary>
/// Telegram API for samples that want to send notifications to Telegram.
/// </summary>
public class Telegram : IAsyncDisposable
{
    /// <summary>Maximum length of a Telegram message.</summary>
    private const int MaxMessageLength = 4096;

    /// <summary>URL-encoded Telegram group ID to which to send messages.</summary>
    private readonly string groupId;

    /// <summary>Telegram API token.</summary>
    private readonly string apiToken;

    /// <summary>HTTP client to use to send messages to Telegram, or <c>null</c> to create a new instance.</summary>
    private readonly HttpClient httpClient;

    /// <summary><c>true</c> if <see cref="httpClient"/> was created by this instance, <c>false</c> otherwise.</summary>
    private readonly bool disposeHttpClient;

    /// <summary>Lock object to be used when accessing <see cref="currentBatch"/> and <see cref="batchTimer"/>.</summary>
    private readonly Lock batchLock;

    /// <summary>List of messages in the current batch.</summary>
    /// <remarks>All access has to be protected by <see cref="batchLock"/>.</remarks>
    private readonly List<string> currentBatch;

    /// <summary>Lock object to be used when accessing <see cref="disposedValue"/>.</summary>
    private readonly Lock disposedValueLock;

    /// <summary>Set to <c>true</c> if the object was disposed already, <c>false</c> otherwise. Used by the dispose pattern.</summary>
    /// <remarks>All access has to be protected by <see cref="disposedValueLock"/>.</remarks>
    private bool disposedValue;

    /// <summary>Timer for the current batch, or <c>null</c> if there is no batch at the moment.</summary>
    /// <remarks>All access has to be protected by <see cref="batchLock"/>.</remarks>
    private System.Timers.Timer? batchTimer;

    /// <summary>
    /// Creates a new instance of the object.
    /// </summary>
    /// <param name="groupId">Telegram group ID to which to send messages.</param>
    /// <param name="apiToken">Telegram API token.</param>
    /// <param name="httpClient">HTTP client to use to send messages to Telegram, or <c>null</c> to create a new instance.</param>
    public Telegram(string groupId, string apiToken, HttpClient? httpClient = null)
    {
        this.batchLock = new();
        this.disposedValueLock = new();
        this.groupId = HttpUtility.UrlEncode(groupId);
        this.apiToken = apiToken;

        if (httpClient is null)
        {
            this.httpClient = new();
            this.disposeHttpClient = true;
        }
        else this.httpClient = httpClient;

        this.currentBatch = new();
    }

    /// <summary>
    /// Sends a message to the Telegram group.
    /// </summary>
    /// <param name="message">Message to send. Note that the message is expected to be an HTML-encoded message.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>If the function succeeds, the return value is <c>null</c>. Otherwise, the return value is an error message.</returns>
    public async Task<string?> SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        string[] parts = SplitStringByMaxLength(message, MaxMessageLength);

        string? error = null;
        foreach (string part in parts)
        {
            string encodedMessagePart = HttpUtility.UrlEncode(message);

            string uri = $"https://api.telegram.org/bot{this.apiToken}/sendMessage?chat_id={this.groupId}&parse_mode=html&text={encodedMessagePart}";

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                using HttpResponseMessage response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    error = $"Sending an HTTP request to Telegram failed with HTTP status code {response.StatusCode}. Response content:{Environment.NewLine}{responseContent}";
            }
            catch (HttpRequestException e)
            {
                error = $"Sending an HTTP request to Telegram failed with HTTP request error {e.HttpRequestError}: {e.Message}";
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (error is not null)
                break;
        }

        return error;
    }

    /// <summary>
    /// Sends a batched message to the Telegram group. A batched message is a message that is not sent immediately, but rather it is batched with other messages within certain time
    /// period. If there are currently no batched messages, the first batch messages starts a new batch and creates a timer. When the timer expires, all the batched messages
    /// are merged and sent at once.
    /// </summary>
    /// <param name="message">Message to send. Note that the message is expected to be an HTML-encoded message.</param>
    /// <param name="batchTimeSpan">Time period of the batch. This is only relevant for the first batched message.</param>
    /// <param name="resultAction">Action to execute when the batch is sent. This is only relevant for the first batched message.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    public void SendBatchedMessage(string message, TimeSpan batchTimeSpan, Action<string?> resultAction, CancellationToken cancellationToken)
    {
        lock (this.batchLock)
        {
            this.currentBatch.Add(message);

            if (this.currentBatch.Count == 1)
            {
                if (this.batchTimer is not null)
                {
                    this.batchTimer.Stop();
                    this.batchTimer.Dispose();
                }

                this.batchTimer = new()
                {
                    AutoReset = false,
                    Interval = batchTimeSpan.TotalMilliseconds,
                    Enabled = false,
                };

                this.batchTimer.Elapsed += async (object? sender, ElapsedEventArgs e) =>
                {
                    string batchedMessage;
                    lock (this.batchLock)
                    {
                        if (this.batchTimer is not null)
                        {
                            this.batchTimer.Dispose();
                            this.batchTimer = null;
                        }

                        batchedMessage = string.Join("\r\n\r\n", this.currentBatch);
                        this.currentBatch.Clear();
                    }

                    string? result = await this.SendMessageAsync(batchedMessage, cancellationToken).ConfigureAwait(false);
                    resultAction(result);
                };

                this.batchTimer.Start();
            }
        }
    }

    /// <summary>
    /// Split string into chunks of a maximum length.
    /// </summary>
    /// <param name="input">Input string to split.</param>
    /// <param name="maxLength">Maximum length of each output string.</param>
    /// <returns>List of strings that together form the input string and are all <paramref name="maxLength"/> long except for the last one, which can shorter.</returns>
    private static string[] SplitStringByMaxLength(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<string>();

        // Calculate the number of chunks needed.
        int chunkCount = (int)Math.Ceiling((double)input.Length / maxLength);
        string[] result = new string[chunkCount];

        for (int i = 0; i < chunkCount; i++)
        {
            int startIndex = i * maxLength;
            int length = Math.Min(maxLength, input.Length - startIndex);
            result[i] = input.Substring(startIndex, length);
        }

        return result;
    }

    /// <summary>
    /// Sends image to the Telegram group as a photo message.
    /// </summary>
    /// <param name="imageBytes">Binary representation of the image to send. Common image formats such as .PNG or .JPG are supported.</param>
    /// <param name="caption">Image caption.</param>
    /// <param name="filename">Name of the image file.</param>
    /// <param name="mediaTypeHeader">Image type. See <see cref="MediaTypeNames.Image"/> for possible values.</param>
    /// <param name="sendAsDocument">
    /// <c>true</c> to send the picture as a document, <c>false</c> to send as a photo. If this is <c>true</c> the image will not be converted to low quality JPEG by Telegram
    /// but it may not have a preview in the Telegram client.
    /// </param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>If the function succeeds, the return value is <c>null</c>. Otherwise, the return value is an error message.</returns>
    public async Task<string?> SendImageAsync(byte[] imageBytes, string caption, string filename, string mediaTypeHeader, bool sendAsDocument, CancellationToken cancellationToken)
    {
        using MultipartFormDataContent content = new();

        using StringContent groupIdContent = new(this.groupId);
        content.Add(groupIdContent, "chat_id");

        using StringContent parseModeContent = new("html");
        content.Add(parseModeContent, "parse_mode");

        using StringContent captionContent = new(caption);
        content.Add(captionContent, "caption");

        using ByteArrayContent imageContent = new(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaTypeHeader);

        content.Add(imageContent, name: sendAsDocument ? "document" : "photo", fileName: filename);

        string? error = null;
        try
        {
            string action = sendAsDocument ? "sendDocument" : "sendPhoto";
            Uri uri = new($"https://api.telegram.org/bot{this.apiToken}/{action}");

            using HttpResponseMessage response = await this.httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            string responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                error = $"Sending an image HTTP request to Telegram failed with HTTP status code {response.StatusCode}. Response content:{Environment.NewLine}{responseContent}";
        }
        catch (HttpRequestException e)
        {
            error = $"Sending an image HTTP request to Telegram failed with HTTP request error {e.HttpRequestError}: {e.Message}";
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return error;
    }

    /// <summary>
    /// Frees managed resources used by the object.
    /// </summary>
    /// <returns>A <see cref="ValueTask">task</see> that represents the asynchronous dispose operation.</returns>
    protected virtual ValueTask DisposeCoreAsync()
    {
        lock (this.disposedValueLock)
        {
            if (this.disposedValue)
                return default;

            this.disposedValue = true;
        }

        lock (this.batchLock)
        {
            if (this.batchTimer is not null)
            {
                this.batchTimer.Stop();
                this.batchTimer.Dispose();
                this.batchTimer = null;
            }
        }

        if (this.disposeHttpClient)
            this.httpClient.Dispose();

        return default;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.DisposeCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace UrlTestApi.Controllers
{
    [Route("api/[controller]")]
    [EnableRateLimiting("concurrencyPolicy")]
    [ApiController]
    public class FetchDataController : ControllerBase
    {
        private static readonly HttpClient httpClient = new HttpClient()
        {
            MaxResponseContentBufferSize = 10000000
        };

        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(5); // Limit to 5 concurrent requests

        /// <summary>
        /// fetchUrlsFromFileAsync web API reads the URL from a file one by one and writes its URL, Response codes, and content in another file
        /// </summary>
        /// <returns></returns>
        [HttpGet("fetchUrlsFromFileAsync")]
        public async Task<IActionResult> FetchUrlsFromFileAsync()
        {
            IEnumerable<string> urls;

            try
            {
                // File in which URL, Response codes, and content will be written
                var fileWritePath = Path.Combine(AppContext.BaseDirectory, "Files", "UrlResponse.txt");

                // File from which URLs will get read
                var fileReadPath = Path.Combine(AppContext.BaseDirectory, "Files", "Urls.txt");

                if (System.IO.File.Exists(fileReadPath))
                {
                    urls = await System.IO.File.ReadAllLinesAsync(fileReadPath);
                    List<Task> fetchTasks = urls.Where(url => !string.IsNullOrWhiteSpace(url))
                                                .Select(url => FetchUrlContentAsync(url, fileWritePath))
                                                .ToList();
                    await Task.WhenAll(fetchTasks); // Wait for all tasks to complete
                    return Ok("Finished fetching URLs.");
                }
                else
                {
                    return NotFound("URLs file not found.");
                }
            }
            catch (FileNotFoundException fileEx)
            {
                return StatusCode(404, $"The file 'Urls.txt' was not found: {fileEx.Message}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// FetchUrlContentAsync function reads the URL response and writes into the file asynchronously
        /// </summary>
        /// <param name="url"></param>
        /// <param name="fileWritePath"></param>
        /// <returns></returns>
        private static readonly object fileLock = new object(); // A lock object to protect the file writes

        private async Task FetchUrlContentAsync(string url, string fileWritePath)
        {
            await semaphore.WaitAsync(); // Ensure limited concurrency
            try
            {
                // Fetch the content asynchronously.
                HttpResponseMessage response = await httpClient.GetAsync(url);

                // Read the response content
                string content = await response.Content.ReadAsStringAsync();
                int statusCode = (int)response.StatusCode;

                // Check for HTTP errors (4xx, 5xx status codes)
                if (statusCode >= 400 && statusCode <= 599)
                {
                    await LogError(url, statusCode, $"HTTP Error: {response.ReasonPhrase}", fileWritePath);
                    return;
                }

                // Ensure the file exists and write the results
                if (!System.IO.File.Exists(fileWritePath))
                {
                    await System.IO.File.WriteAllTextAsync(fileWritePath, "URL,ResponseCode,Content\n");
                }

                // Use a lock to ensure that only one thread writes to the file at a time
                lock (fileLock)
                {
                    // Append result for the current URL
                    using (StreamWriter writer = new StreamWriter(fileWritePath, append: true))
                    {
                        writer.WriteLine($"URL: {url}");
                        writer.WriteLine($"Response Code: {statusCode}"); // Actual HTTP status code
                        writer.WriteLine(content); // Actual response content
                        writer.WriteLine("****************************************************************************************************************");
                    }
                }
            }
            catch (HttpRequestException reqEx)
            {
                // Handle SSL/TLS or connection-related errors
                if (reqEx.InnerException is System.Net.Sockets.SocketException socketEx)
                {
                    // SSL/TLS issues (e.g., connection could not be established)
                    await LogError(url, -100, $"SSL/TLS Error: {socketEx.Message}", fileWritePath); // Error code -100 for SSL errors
                }
                else
                {
                    // General HTTP request failure (network issues, unreachable server, etc.)
                    await LogError(url, -102, $"Network Error: {reqEx.Message}", fileWritePath); // Error code -102 for network-related issues
                }
            }
            catch (TaskCanceledException timeoutEx)
            {
                // Log timeout error with custom error code
                await LogError(url, -101, $"Request timeout: {timeoutEx.Message}", fileWritePath); // Error code -101 for timeouts
            }
            catch (TimeoutException timeoutEx)
            {
                // Handle timeout errors
                await LogError(url, -101, $"Request timed out: {timeoutEx.Message}", fileWritePath); // Error code -101 for timeouts
            }
            catch (System.Net.Sockets.SocketException socketEx)
            {
                // Handle socket errors (DNS issues, unreachable server)
                await LogError(url, -102, $"Socket error: {socketEx.Message}", fileWritePath); // Error code -102 for socket errors
            }
            catch (Exception ex)
            {
                // Log any other unexpected exceptions with a generic error code
                await LogError(url, -199, $"Unexpected system error: {ex.Message}", fileWritePath); // Error code -199 for unexpected system errors
            }
            finally
            {
                semaphore.Release(); // Release the semaphore slot
            }
        }

        private async Task LogError(string url, int errorCode, string errorMessage, string fileWritePath)
        {
            // Ensure the file exists and write the results
            if (!System.IO.File.Exists(fileWritePath))
            {
                await System.IO.File.WriteAllTextAsync(fileWritePath, "URL,ResponseCode,Content\n");
            }

            // Use a lock to ensure that only one thread writes to the file at a time
            lock (fileLock)
            {
                // Append result for the error case
                using (StreamWriter writer = new StreamWriter(fileWritePath, append: true))
                {
                    writer.WriteLine($"URL: {url}");
                    writer.WriteLine($"Response Code: {errorCode}"); // Custom error code
                    writer.WriteLine(errorMessage); // Detailed error message
                    writer.WriteLine("****************************************************************************************************************");
                }
            }
        }




        /// <summary>
        /// Log the error for a failed URL
        /// </summary>

    }
}

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http.Headers;

namespace OAIPreRouter.Cli.TestClient
{
    class Program
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        static async Task Main(string[] args)
        {
            Console.WriteLine("OAIPreRouter.Cli Test Client Started");
            Console.WriteLine("----------------------------------");

            string baseUrl = "http://127.0.0.1:7071"; // Adjust URL if needed

            await TestHealthEndpoint(baseUrl);

            Console.WriteLine("----------------------------------");
            await TestModelsEndpoint(baseUrl);

            Console.WriteLine("----------------------------------");
            await TestChatCompletionsRouting(baseUrl);

            Console.WriteLine("----------------------------------");
            await TestChatCompletionsValidity(baseUrl);

            Console.WriteLine("----------------------------------");
            Console.WriteLine("Testing completed. Press any key to exit.");
        }

        static async Task TestHealthEndpoint(string baseUrl)
        {
            Console.WriteLine($"[Test] Running Health Endpoint Test against {baseUrl}/health");
            try
            {
                var response = await HttpClient.GetAsync($"{baseUrl}/health");
                Console.WriteLine($"[INFO] Status Code: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[SUCCESS] Response: {content}");
                }
                else
                {
                    Console.WriteLine($"[FAILURE] Health check failed with status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Health endpoint test failed: {ex.Message}");
            }
        }

        static async Task TestModelsEndpoint(string baseUrl)
        {
            Console.WriteLine($"[Test] Running Models Endpoint Test against {baseUrl}/v1/models");
            try
            {
                var response = await HttpClient.GetAsync($"{baseUrl}/v1/models");
                Console.WriteLine($"[INFO] Status Code: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[SUCCESS] Response: {content}");
                }
                else
                {
                    Console.WriteLine($"[FAILURE] Models endpoint failed with status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Models endpoint test failed: {ex.Message}");
            }
        }

        static async Task RunChatCompletionTestWithRequest(HttpRequestMessage request)
        {
            try
            {
                var response = await HttpClient.SendAsync(request);
                Console.WriteLine($"[INFO] Status Code: {response.StatusCode}");
                var detectHeader = response.Headers.TryGetValues("X-PreRouter-Detect", out var detectValues) ? detectValues.FirstOrDefault() : "N/A";
                var laneHeader = response.Headers.TryGetValues("X-PreRouter-Intended-Lane", out var laneValues) ? laneValues.FirstOrDefault() : "N/A";
                Console.WriteLine($"[INFO] X-PreRouter-Detect: {detectHeader}");
                Console.WriteLine($"[INFO] X-PreRouter-Intended-Lane: {laneHeader}");
                if (response.IsSuccessStatusCode)
                    Console.WriteLine("[SUCCESS] Request completed.");
                else
                    Console.WriteLine($"[FAILURE] Request failed with status: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Chat completion test failed: {ex.Message}");
            }
        }

        static async Task TestChatCompletionsRouting(string baseUrl)
        {
            Console.WriteLine($"[Test] Running Chat Completions Routing Test against {baseUrl}/v1/chat/completions");

            // Scenario 1: Main agent payload (large system prompt ~100KB)
            Console.WriteLine("\n[Scenario 1] Main Agent Payload (Expected: MAIN_AGENT)");
            var largeSystemContent = new string('x', 100000); // ~100KB system prompt
            var mainAgentBody = $"{{\"model\":\"qwen-3.6-27b\",\"messages\":[{{\"role\":\"system\",\"content\":\"{largeSystemContent}\"}},{{\"role\":\"user\",\"content\":\"Hello, are you there?\"}}]}}";
            await RunChatCompletionTest(baseUrl, mainAgentBody);

            // Scenario 2: Sub-agent small payload (should be classified as fast lane)
            Console.WriteLine("\n[Scenario 2] Sub-Agent Small Payload (Expected: SUB_AGENT, fast)");
            var smallBody = "{\"model\":\"qwen-3.5-9b\",\"messages\":[{\"role\":\"system\",\"content\":\"You are a code explorer. Search for files.\"},{\"role\":\"user\",\"content\":\"Find all files containing 'Authentication'\"}]}";
            await RunChatCompletionTest(baseUrl, smallBody);

            // Scenario 3: Sub-agent large payload (should be classified as heavy lane)
            Console.WriteLine("\n[Scenario 3] Sub-Agent Large Payload (Expected: SUB_AGENT, heavy)");
            var largeUserContent = new string('y', 70000); // ~70KB user content
            var largeSubAgentBody = $"{{\"model\":\"qwen-3.6-35b\",\"messages\":[{{\"role\":\"system\",\"content\":\"You are a complex task agent.\"}},{{\"role\":\"user\",\"content\":\"{largeUserContent}\"}}]}}";
            await RunChatCompletionTest(baseUrl, largeSubAgentBody);
        }

        static async Task RunChatCompletionTest(string baseUrl, string body)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = await HttpClient.SendAsync(request);

                Console.WriteLine($"[INFO] Status Code: {response.StatusCode}");

                var detectHeader = response.Headers.Contains("X-PreRouter-Detect")
                    ? response.Headers.GetValues("X-PreRouter-Detect").FirstOrDefault()
                    : "N/A";

                var laneHeader = response.Headers.Contains("X-PreRouter-Intended-Lane")
                    ? response.Headers.GetValues("X-PreRouter-Intended-Lane").FirstOrDefault()
                    : "N/A";

                Console.WriteLine($"[INFO] X-PreRouter-Detect: {detectHeader}");
                Console.WriteLine($"[INFO] X-PreRouter-Intended-Lane: {laneHeader}");

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("[SUCCESS] Request completed.");
                }
                else
                {
                    Console.WriteLine($"[FAILURE] Request failed with status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Chat completion test failed: {ex.Message}");
            }
        }

        static async Task TestChatCompletionsValidity(string baseUrl)
        {
            Console.WriteLine($"[Test] Running Chat Completions Validity Test against {baseUrl}/v1/chat/completions");

            var requestBody = new
            {
                model = "qwen-3.6-27b",
                messages = new[]
                {
                    new { role = "user", content = "Hello, are you there?" }
                }
            };

            try
            {
                var response = await HttpClient.PostAsJsonAsync($"{baseUrl}/v1/chat/completions", requestBody);
                Console.WriteLine($"[INFO] Status Code: {response.StatusCode}");

                var detectHeader = response.Headers.Contains("X-PreRouter-Detect")
                    ? response.Headers.GetValues("X-PreRouter-Detect").FirstOrDefault()
                    : "N/A";

                var laneHeader = response.Headers.Contains("X-PreRouter-Intended-Lane")
                    ? response.Headers.GetValues("X-PreRouter-Intended-Lane").FirstOrDefault()
                    : "N/A";

                Console.WriteLine($"[INFO] X-PreRouter-Detect: {detectHeader}");
                Console.WriteLine($"[INFO] X-PreRouter-Intended-Lane: {laneHeader}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    // Basic validation: check if it contains a response message
                    if (content.Contains("message") && content.Contains("content"))
                    {
                        Console.WriteLine("[SUCCESS] Response integrity verified.");
                    }
                    else
                    {
                        Console.WriteLine("[FAILURE] Response integrity check failed: unexpected content format.");
                    }
                }
                else
                {
                    Console.WriteLine($"[FAILURE] Chat completions validity test failed with status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Chat completions validity test failed: {ex.Message}");
            }
        }
    }
}
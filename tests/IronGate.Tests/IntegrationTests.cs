using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IronGate.Tests;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.Development.json", optional: false);
                config.AddEnvironmentVariables();
            });
        });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Healthz_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/healthz");
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("healthy", content.GetProperty("status").GetString());
        Assert.Equal("IronGate", content.GetProperty("service").GetString());
    }

    [Fact]
    public async Task PublicRoute_WithoutToken_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/public/echo");
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedRoute_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/protected/echo");
        
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedRoute_WithValidToken_ReturnsOkAndInjectsHeaders()
    {
        var token = await GetAccessTokenAsync();
        
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected/echo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _client.SendAsync(request);
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        Assert.True(response.Headers.Contains("x-forwarded-user"));
        Assert.True(response.Headers.Contains("x-forwarded-roles"));
        Assert.True(response.Headers.Contains("x-correlation-id"));
    }

    [Fact]
    public async Task CorrelationId_HeaderPropagatedToDownstream()
    {
        var token = await GetAccessTokenAsync();
        var correlationId = "test-correlation-123";
        
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected/echo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("x-correlation-id", correlationId);
        
        var response = await _client.SendAsync(request);
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("x-correlation-id", out var values));
        Assert.Equal(correlationId, values.FirstOrDefault());
    }

    [Fact]
    public async Task MetricsEndpoint_ReturnsPrometheusFormat()
    {
        var response = await _client.GetAsync("/metrics");
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        
        var content = await response.Content.ReadAsStringAsync();
        // Just verify we get some Prometheus metrics output
        Assert.Contains("# TYPE", content);
        Assert.Contains("# HELP", content);
    }

    private async Task<string> GetAccessTokenAsync()
    {
        var keycloakUrl = "http://localhost:8080/realms/master/protocol/openid-connect/token";
        
        var formData = new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = "admin",
            ["grant_type"] = "password",
            ["client_id"] = "irongate-test",
            ["client_secret"] = "test-secret"
        };

        using var httpClient = new HttpClient();
        var response = await httpClient.PostAsync(keycloakUrl, new FormUrlEncodedContent(formData));
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        return json.GetProperty("access_token").GetString()!;
    }
}
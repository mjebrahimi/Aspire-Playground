using System.Net;

namespace aspire_starter.Tests.Tests;

public class IntegrationTest1
{
    [Fact]
    public async Task ApiServiceShouldReturnsOkStatusCode()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.aspire_starter_AppHost>();
        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        // Act
        var httpClient = app.CreateHttpClient("apiservice");
        var response = await httpClient.GetAsync("/weatherforecast");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
using EasyCaching.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

builder.Services.AddHttpContextAccessor();

builder.AddRedisClient("redis");

//Uses AddDbContextPool internally for performance reasons.
builder.AddSqlServerDbContext<AppDbContext>("database", null, option =>
{
    if (builder.Environment.IsDevelopment())
    {
        option.EnableDetailedErrors();
        option.EnableSensitiveDataLogging();
    }
});

builder.Services.AddLZ4Compressor("lz4");
builder.Services.AddEasyCaching(options =>
{
    options
        .UseRedis(config => config.SerializerName = "msgpack")
        .WithMessagePack("msgpack")
        .WithCompressor("lz4");
});

// Add services to the container.
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.Services.InitializeAsync();

app.Services.SetEasyCachingRedisConnectionMultiplexer();

app.UseSerilogRequestLogging(options => options.IncludeQueryInRequestPath = true);

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", async ([FromServices] IServiceProvider serviceProvider) =>
{
    var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
    return await dbContext.People.ToListAsync();

    //try
    //{
    //    await using (var scope1 = serviceProvider.CreateAsyncScope())
    //    {
    //        var dbContext1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
    //        var person1 = await dbContext1.People.FindAsync(1);
    //        await using (var scope2 = serviceProvider.CreateAsyncScope())
    //        {
    //            var dbContext2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
    //            var person2 = await dbContext2.People.FindAsync(1);
    //            person2!.Name += "_Edited";
    //            var res2 = await dbContext2.SaveChangesAsync();
    //        }
    //        person1!.Name += "_Edited";
    //        var res1 = await dbContext1.SaveChangesAsync();
    //    }
    //}
    //catch (Exception ex)
    //{
    //    throw;
    //}

    //var easyCaching = serviceProvider.GetRequiredService<IEasyCachingProvider>();
    //await easyCaching.SetAsync("mykey", "myvalue", TimeSpan.FromMinutes(1));
    //var myvalue = await easyCaching.GetAsync<string>("mykey");

    //throw new NotImplementedException();

    //var forecast = Enumerable.Range(1, 5).Select(index =>
    //    new WeatherForecast
    //    (
    //        DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
    //        Random.Shared.Next(-20, 55),
    //        summaries[Random.Shared.Next(summaries.Length)]
    //    ))
    //    .ToArray();

    //return forecast;
});

app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(this IServiceProvider serviceProvider)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        //await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        if (await dbContext.People.AnyAsync() is false)
        {
            dbContext.People.AddRange(
               new Person { Name = "Alice" },
               new Person { Name = "Bob" },
               new Person { Name = "Charlie" }
            );

            await dbContext.SaveChangesAsync();
        }
    }
}
public static class EasyCachingRedisExtensions
{
    public static void SetEasyCachingRedisConnectionMultiplexer(this IServiceProvider serviceProvider)
    {
        var redisDatabaseProvider = serviceProvider.GetRequiredService<IRedisDatabaseProvider>();
        var connectionMultiplexer = serviceProvider.GetRequiredService<IConnectionMultiplexer>();
        redisDatabaseProvider.SetConnectionMultiplexer(connectionMultiplexer);
    }

    public static void SetConnectionMultiplexer(this IRedisDatabaseProvider redisDatabaseProvider, IConnectionMultiplexer connectionMultiplexer)
    {
        _connectionMultiplexer((RedisDatabaseProvider)redisDatabaseProvider) = new((ConnectionMultiplexer)connectionMultiplexer);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_connectionMultiplexer")]
    private extern static ref Lazy<ConnectionMultiplexer> _connectionMultiplexer(RedisDatabaseProvider @this);

}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Person> People { get; set; }
}

public class Person
{
    public int Id { get; set; }
    public string Name { get; set; }

    [Timestamp]
    public byte[] Timestamp { get; set; }
}
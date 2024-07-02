using EasyCaching.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

builder.Services.AddHttpContextAccessor();

builder.AddRedisClient("redis");

//Uses AddDbContextPool internally for performance reasons.
builder.AddSqlServerDbContext<AppDbContext>("database",
    config => config.DisableRetry = true, //It's necessary for using database transactions
    option =>
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

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// Add services to the container.
//builder.Services.AddProblemDetails();

var app = builder.Build();

await app.Services.InitializeAsync();

app.Services.SetEasyCachingRedisConnectionMultiplexer();

app.UseSerilogRequestLogging(options => options.IncludeQueryInRequestPath = true);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
    app.UseExceptionHandler();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", async ([FromServices] IServiceProvider serviceProvider, [FromQuery] bool wait = false) =>
{
    var dbContext = serviceProvider.GetRequiredService<AppDbContext>();

    await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

    var newAppointment = wait ? new Appointment
    {
        PersonId = 1,
        Title = "Appointment_Alice_3",
        Start = DateTime.Parse("2024-07-26 10:00"),
        End = DateTime.Parse("2024-07-26 11:00")
    } : new Appointment
    {
        PersonId = 1,
        Title = "Appointment_Alice_3",
        Start = DateTime.Parse("2024-07-26 11:00"),
        End = DateTime.Parse("2024-07-26 12:00")
    };

    //Use operators <= and >= if touching boundaries are considered as overlap.
    var isOverlapped = await dbContext.Appointments.AnyAsync(p => newAppointment.Start < p.Start && p.End > newAppointment.End);

    if (wait)
        await Task.Delay(10000);

    if (isOverlapped)
        throw new Exception("The appointment time is overlapped with another one.");

    dbContext.Appointments.Add(newAppointment);

    try
    {
        var result = await dbContext.SaveChangesAsync();
    }
    catch (InvalidOperationException ex)
    when (ex.InnerException is DbUpdateException { InnerException: SqlException sqlException }
        && sqlException.Message.Contains("deadlocked on lock resources with another process"))
    {
        throw new Exception("The appointment time is overlapped with another one.");
    }

    await transaction.CommitAsync();

    return await dbContext.Appointments.ToListAsync();

    #region Comment
    //var query = dbContext
    //    .People
    //    .Include(p => p.Appointments)
    //    .Where(p => p.Appointments.First().Title == "Appointment_Alice_1");
    //var querystring = query.ToQueryString();
    //var result = await query.ToListAsync();
    //return result;

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
    #endregion
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
        await Task.Delay(10000);

        await using var scope = serviceProvider.CreateAsyncScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        //await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        ////Write an sql user defined Function to check date range overlap
        //await dbContext.Database.ExecuteSqlRawAsync($@"
        //CREATE FUNCTION [dbo].[IsDateRangeOverlapped] (@Start DATETIME2, @End DATETIME2, @Id INT)
        //RETURNS BIT
        //AS
        //BEGIN
        //    -- Use operators <= and >= if touching boundaries are considered as overlap.
        //    RETURN CASE WHEN EXISTS(SELECT 1 FROM [Appointments] WHERE @Start < [{nameof(Appointment.End)}] AND @End > [{nameof(Appointment.Start)}] AND Id != @Id) THEN 1 ELSE 0 END
        //END");

        ////Add check constraint to check date range overlap
        //await dbContext.Database.ExecuteSqlRawAsync($@"
        //ALTER TABLE [Appointments]
        //ADD CONSTRAINT [CK_DateRangeOverlap] CHECK ([dbo].[IsDateRangeOverlapped]([{nameof(Appointment.Start)}],[{nameof(Appointment.End)}],[{nameof(Appointment.Id)}]) = 0)");

        if (await dbContext.People.AnyAsync() is false)
        {
#pragma warning disable S6966 // Awaitable method should be used
            dbContext.People.AddRange(
               new Person
               {
                   Name = "Alice",
                   Appointments =
                   [
                       new Appointment { Title = "Appointment_Alice_1", Start = DateTime.Parse("2024-07-20 10:00"), End = DateTime.Parse("2024-07-20 11:00") },
                       new Appointment { Title = "Appointment_Alice_2", Start = DateTime.Parse("2024-07-20 12:00"), End = DateTime.Parse("2024-07-20 13:00") },
                   ]
               },
               new Person
               {
                   Name = "Bob",
                   //Appointments =
                   //[
                   //    new Appointment { Title = "Appointment_Bob_1" },
                   //    new Appointment { Title = "Appointment_Bob_2" },
                   //]
               },
               new Person
               {
                   Name = "Charlie",
                   //Appointments =
                   //[
                   //    new Appointment { Title = "Appointment_Charlie_1" },
                   //    new Appointment { Title = "Appointment_Charlie_2" },
                   //]
               }
            );
#pragma warning restore S6966 // Awaitable method should be used

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
    public DbSet<Appointment> Appointments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}

public class Person
{
    public int Id { get; set; }
    public string Name { get; set; }

    public ICollection<Appointment> Appointments { get; set; }

    [Timestamp]
    public byte[] Timestamp { get; set; }
}

public class Appointment
{
    public int Id { get; set; }
    public string Title { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    public int PersonId { get; set; }
    public Person Person { get; set; }
}
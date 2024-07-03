var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");
var sqlserver = builder.AddSqlServer("database");

builder.AddProject<Projects.aspire_starter_ApiService>("apiservice")
    .WithReference(redis)
    .WithReference(sqlserver);

builder.Build().Run();
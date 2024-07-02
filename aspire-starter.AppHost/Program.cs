var builder = DistributedApplication.CreateBuilder(args);

var seq = builder.AddSeq("seq");
var redis = builder.AddRedis("redis");
var sqlserver = builder.AddSqlServer("sqlserver").AddDatabase("database");

var apiservice = builder.AddProject<Projects.aspire_starter_ApiService>("apiservice")
    .WithReference(seq)
    .WithReference(redis)
    .WithReference(sqlserver);

builder.AddProject<Projects.aspire_starter_Web>("webfrontend")
    .WithReference(apiservice)
    .WithReference(seq);

builder.Build().Run();
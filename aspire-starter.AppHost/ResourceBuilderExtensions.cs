public static class ResourceBuilderExtensions
{
    public static IResourceBuilder<IResourceWithConnectionString> AddSqlServer(this IDistributedApplicationBuilder builder, string name)
    {
        var localSqlConnection = builder.Configuration["LocalSqlServerConnection"];

        if (string.IsNullOrWhiteSpace(localSqlConnection) is false)
        {
            //Connect to local SqlServer instead of container SqlServer for faster running/debugging the project
            builder.Configuration[$"ConnectionStrings:{name}"] = localSqlConnection;
            return builder.AddConnectionString(name);
        }

        return SqlServerBuilderExtensions.AddSqlServer(builder, "sqlserver").AddDatabase(name);
    }

    public static IResourceBuilder<IResourceWithConnectionString> AddRedis(this IDistributedApplicationBuilder builder, string name)
    {
        var localRedisConnection = builder.Configuration["LocalRedisConnection"];

        if (string.IsNullOrWhiteSpace(localRedisConnection) is false)
        {
            //Connect to local Redis instead of container Redis for faster running/debugging the project
            builder.Configuration[$"ConnectionStrings:{name}"] = localRedisConnection;
            return builder.AddConnectionString(name);
        }

        return RedisBuilderExtensions.AddRedis(builder, name);
    }
}
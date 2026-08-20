using FaceUnlock.Service;

Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "FaceUnlock Service")
    .ConfigureServices(services =>
    {
        services.AddHostedService(provider =>
            new UnlockWorker(provider.GetRequiredService<ILogger<UnlockWorker>>()));
    })
    .Build()
    .Run();

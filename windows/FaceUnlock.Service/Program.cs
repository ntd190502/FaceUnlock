using FaceUnlock.Service;

Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "FaceUnlock Service")
    .ConfigureLogging(logging =>
    {
        logging.AddProvider(new ServiceFileLoggerProvider());
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService(provider => new UnlockWorker(provider.GetRequiredService<ILogger<UnlockWorker>>()));
    })
    .Build()
    .Run();

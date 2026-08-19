using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FaceUnlock.Core;

namespace FaceUnlock.Service;
public sealed class UnlockWorker(ILogger<UnlockWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("FaceUnlock service started");
        while(!stoppingToken.IsCancellationRequested)
        {
            try{using var pipe=new NamedPipeServerStream("FaceUnlock.Control",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);await pipe.WaitForConnectionAsync(stoppingToken);using var sr=new StreamReader(pipe,Encoding.UTF8,false,1024,true);using var sw=new StreamWriter(pipe,Encoding.UTF8,1024,true){AutoFlush=true};var cmd=await sr.ReadLineAsync(stoppingToken);if(cmd=="ping")await sw.WriteLineAsync("pong");else await sw.WriteLineAsync(JsonSerializer.Serialize(new{ok=false,error="Credential Provider integration command not enabled in safe MVP"}));}catch(OperationCanceledException){}catch(Exception ex){log.LogError(ex,"Pipe loop failed");await Task.Delay(1000,stoppingToken);}
        }
    }
}

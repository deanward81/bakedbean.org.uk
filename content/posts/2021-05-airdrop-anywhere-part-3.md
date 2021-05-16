---
title: "AirDrop Anywhere - Part 3 - Receiving files"
date: 2021-05-17T15:00:00Z
tags: [.net, networking, airdrop, apple]
images: [img/airdrop-anywhere-cover.jpg]
summary: "Part three of the journey towards implementing AirDrop on any platform using .NET Core. This episode we cover receiving files from one Apple device to another using a .NET implementation of AirDrop."
---

> This is part 3 of a series of posts:
> - [Part 1 - Introduction](/posts/2021-05-airdrop-anywhere-part-1)
> - [Part 2 - Writing some code](/posts/2021-05-airdrop-anywhere-part-2)
> - [Part 3 - Receiving files](/posts/2021-05-airdrop-anywhere-part-3)
>     - [GitHub (File receiving bits)](https://github.com/deanward81/AirDropAnywhere/tree/2021-05-17-receiving-files)
> - [GitHub (latest)](https://github.com/deanward81/AirDropAnywhere/tree/main) - **NOTE** still work in progress!

In the [previous episode](/posts/2021-05-airdrop-anywhere-part-2) we talked about the challenges I came across while implementing the mDNS advertisements necessary to support AirDrop. By the end of this episode we should be able to send a file from an Apple device to another Apple device running our service 🎉.

## How it works

As detailed last time, AirDrop works by advertising the endpoint associated with an HTTPS listener, so we'll need something listening on HTTPS and have it implement the relevant API routes that AirDrop expects. So we need to know what the API surface looks like!

Typically we'd use something like [Fiddler](https://www.telerik.com/fiddler) to intercept HTTPS traffic and decrypt it but, because we're operating over the AWDL interface, we can't realistically configure AirDrop to use our proxy. Our next option is to use [Wireshark](https://www.wireshark.org/) and configure it to decrypt TLS. Fortunately AirDrop supports self-signed certificates for whatever is listening on the HTTPS endpoint - so we can trivially use the ASP.NET Core Development Certificate stored in the keychain and configure Wireshark to use it. Then all we need to do is sniff packets on the listening device's AWDL interface...

However, other than for monitoring what's going on, we don't need to do this to work out the API surface! [PrivateDrop](https://github.com/seemoo-lab/privatedrop) & [OpenDrop](https://github.com/seemoo-lab/opendrop) both provide an implementation of the API that we can translate to the equivalent C#.

### API Endpoints

AirDrop needs 3 endpoints in order to function:

1. `/Discover` - accepts a POST containing information about the sender of the request and expects a return message containing details of the receiver such as its name and capabilities. This route is called immediately after an mDNS request returns the details of our HTTPS endpoint.
1. `/Ask` - accepts a POST containing information about the sender and metadata on files it wants to send. This route is called when the user clicks a recipient in the AirDrop UI and is expected to block until the receiver has confirmed that they want to receive the files.
1. `/Upload` - called once the sender has been authorised to send files. It POSTs a GZIP-encoded body containing the files' data. What format those files are in depends on capabilities returned by the call to `/Discover` and the flags contained in the TXT record advertised using mDNS.

## Spinning up the server

Exposing the HTTPS endpoint we need is as simple as spinning up an instance of Kestrel and having it listen using the default development certificate. Using that certificate is a little hacky but it'll work for now and we can generate our own self-signed certificate later.

This code is heavily cutdown for the blog, but it effectively looks like this in the `AirDropAnywhere.Cli` project:

```c#
WebHost.CreateDefaultBuilder()
    .ConfigureAirDrop(
        options =>
        {
            options.ListenPort = 34553;
        }
    )
    .Build()
    .Run();
```

`ConfigureAirDrop` is an extension method on `IWebHostBuilder` in the `AirDropAnywhere.Core` project - eventually we'll allow any application to expose AirDrop by consuming the `Core` project as a NuGet package which is why it's structured like this.

```c#
public static IWebHostBuilder ConfigureAirDrop(this IWebHostBuilder builder, Action<AirDropOptions>? setupAction = null) => 
    builder
        .ConfigureKestrel(
            options =>
            {
                var airDropOptions = options.ApplicationServices.GetRequiredService<IOptions<AirDropOptions>>();
                
                options.ConfigureEndpointDefaults(
                    options =>
                    {
                        // TODO: use our own self-signed certificate!
                        options.UseHttps();
                    }
                );
                        
                options.ListenAnyIP(airDropOptions.Value.ListenPort);
            }
        )
        .ConfigureServices(
            services =>
            {
                services.AddRouting();
                services.AddAirDrop(setupAction);
            }
        )
        .Configure(
            app =>
            {
                app.UseRouting();
                app.UseEndpoints(
                    endpoints =>
                    {
                        endpoints.MapAirDrop();
                    }
                );
            }
        );
```

This is pretty simple - it configures Kestrel to use HTTPS, for it to listen on any IP address and use endpoint routing. We expose some options that allow us to configure things like the listening port and then call down into extension methods that add relevant services and map the API's endpoints:

```c#
public static IServiceCollection AddAirDrop(this IServiceCollection services, Action<AirDropOptions>? setupAction = null)
{
    services.AddScoped<AirDropRouteHandler>();
    services.AddSingleton<AirDropService>();
    services.AddSingleton<IHostedService>(s => s.GetService<AirDropService>()!);
    services.AddOptions<AirDropOptions>().ValidateDataAnnotations();

    if (setupAction != null)
    {
        services.Configure(setupAction);
    }

    return services;
}

public static IEndpointRouteBuilder MapAirDropCore(IEndpointRouteBuilder endpoints)
{  
    endpoints.MapPost("Discover", ctx => AirDropRouteHandler.ExecuteAsync(ctx, (c, r) => r.DiscoverAsync(c)));
    endpoints.MapPost("Ask", ctx => AirDropRouteHandler.ExecuteAsync(ctx, (c, r) => r.AskAsync(c)));
    endpoints.MapPost("Upload", ctx => AirDropRouteHandler.ExecuteAsync(ctx, (c, r) => r.UploadAsync(c)));
    return endpoints;
}
```

You'll see that we add an `IHostedService` called `AirDropService` - this is what handles our mDNS advertisements and sets up AWDL on macOS. [`IHostedService`](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-5.0&tabs=visual-studio) exposes a `StartAsync` and `StopAsync` that allows us to tie the lifetime of our service to the underlying `WebHost` which makes it perfect for long-running services.

Then, we add some POST endpoints to endpoint routing and have them hook into `AirDropRouteHandler`. `ExecuteAsync` simply does some boilerplate bits per request - it spins up an instance of `AirDropRouteHandler` and then calls an instance method to do the work of handling the request.

It's possible that just moving this to WebApi or MVC would be a better long term bet, but this works fine for now.

### But does it work...

After spinning up the application, sniffing the AWDL interface using Wireshark and gleefully opening AirDrop on my iPhone it... _doesn't_ work. Well, pants 🤦‍♂️. Wireshark indicated that opening the connection to the HTTPS endpoint fails with a timeout. I gave this a little thought and quickly realised that it was for the same reason that my mDNS implementation failed the first time around - I need to explicitly tell the OS that this socket needs to use the AWDL interface. Easy peasy!

Well, I thought that would be the case - surely Kestrel has trivial extensibility points to mess with its underlying sockets? Turns out that isn't the case at all. Kestrel uses an implementation of the `IConnectionListenerFactory` interface called `SocketTransportFactory`. This is responsible for binding to the underlying transport and returning an `IConnectionListener`. I don't particularly want to implement all the plumbing that `SocketTransportFactory` does and, unfortunately, all of its internals are, ummmm, `internal`.

Reflection to the rescue! Yes, this is terrible practice but it gets me where I need to be so I'll take it. Here's what I came up (again, abbreviated for the blog post - you can see the actual implementation [here](https://github.com/deanward81/AirDropAnywhere/blob/main/src/AirDropAnywhere.Core/HttpTransport/AwdlSocketTransportFactory.cs));

```c#
// Actual implementation: https://github.com/deanward81/AirDropAnywhere/blob/main/src/AirDropAnywhere.Core/HttpTransport/AwdlSocketTransportFactory.cs
internal class AwdlSocketTransportFactory : IConnectionListenerFactory
{
    private readonly IConnectionListenerFactory _connectionListenerFactory;
    private readonly FieldInfo _listenSocketField;
    
    public AwdlSocketTransportFactory(IOptions<SocketTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _connectionListenerFactory = new SocketTransportFactory(options, loggerFactory);
        // HACK: this merry little reflective dance is because of sealed internal classes
        // and no extensibility points, yay :/
        _listenSocketField = typeof(SocketTransportFactory).Assembly
            .GetType("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionListener")
            .GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var transport = await _connectionListenerFactory.BindAsync(endpoint, cancellationToken);
        // HACK: fix up the listen socket to support listening on AWDL
        var listenSocket = (Socket?) _listenSocketField.GetValue(transport);
        if (listenSocket != null)
        {
            listenSocket.SetAwdlSocketOption();
        }
        return transport;
    }
}
```

We can tell Kestrel to use this by updating our `AddAirDrop` extension method to use the service, but only on macOS:

```c#
if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    services.AddSingleton<IConnectionListenerFactory, AwdlSocketTransportFactory>();
}
```

And... it works! AirDrop successfully POSTs a message to `/Discover` and we promptly respond with a `200 OK`. Naturally this doesn't make the sender _do_ anything, but it's a start!

## `/Discover`


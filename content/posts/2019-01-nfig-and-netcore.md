---
title: "Integrating NFig with .NET Core"
date: 2019-01-18T00:00:00Z
tags: [.net, nfig, stack-overflow]
---
Over the past few weeks we've been busy laying the groundwork needed to support migrating Stack Overflow to [ASP.NET Core](https://docs.microsoft.com/en-us/aspnet/core/?view=aspnetcore-2.1).

Our roadmap for the migration extends out to later this year, but we've been tackling a lot of pre-requisites:

<!--more-->

- Porting most of our open source and internal libraries to netstandard20 so that they can be used readily across net462 and netcoreapp2x projects
- Migrating from Linq2Sql to EF Core (and [bringing down most of the Stack Exchange Network in the process](https://twitter.com/Nick_Craver/status/1047925037007884288))
- Using the built-in `ManagedWebSocket` and Kestrel instead of [NetGain](https://github.com/StackExchange/NetGain) for our web socket server.
- [Tweaking how we do localization](https://m0sa.net/posts/2018-11-runtime-moonspeak/) to move from a precompilation model to a runtime-based model.

We've also started some greenfield projects that aren't public-facing but that support critical parts of our business. That's helped us put together a list of 'standard' libraries that we'll likely use across some of our other applications when we migrate them to .NET Core. One of those has been [NFig](https://github.com/NFig/NFig) - a way to manage configuration settings for our applications.

## NFig - Some Background

NFig was originally written by [Bret Copeland](https://bret.codes/) and [Bryan Ross](https://rossipedia.com/) for the ad server that powers our job ads. After a while we started using it in other applications such as [Stack Overflow Talent](https://talent.stackoverflow.com/) and [Stack Overflow Jobs](https://stackoverflow.com/jobs/) as well as a few internal projects. It works by allowing us to define our configuration settings in code and then allowing us to change those settings at runtime by storing overrides in a backing store. Here's an example configuration class:

```c#
public class Settings
{
    [SettingsGroup]
    public FeatureFlagSettings FeatureFlags { get; private set; }

    public class FeatureFlagSettings
    {
        [Description("Enables the [Whizzbang](https://trello.com/.../) feature.")]
        [Setting(false)]
        [TieredDefaultValue(Tier.Local, true)]
        [TieredDefaultValue(Tier.Dev, true)]
        public bool EnableWhizzbang { get; private set; }
    }
}
```

Everywhere that we want to use the Whizzbang feature we can check this flag:

```c#
if (Current.Settings.FeatureFlags.EnableWhizzbang)
{
     // do whizzbang stuff!
}
```

NFig surfaces this is in configuration views provided by the [NFig.UI](https://github.com/NFig/NFig.UI) package:

![NFig List]

We can override this at runtime for a specific data centre or everywhere and that value will be immediately reflected the next time it's accessed by the code:

![NFig Setting]

This is great and works extremely well - it allows us to do things like deploy features behind a flag and then switch them on at runtime without having to re-deploy the application.

## .NET Core & NFig

.NET Core comes with its own set of APIs (known as the [`Options` pattern](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-2.2)) for providing upto date configuration settings. An `IOptions` implementation is configured based upon code like this in an application's startup:

```c#
public class Startup
{
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.Configure<MyOptions>(_configuration.GetSection("MyOptions"));
    }
}
```

This uses the configuration subsystem to compose the options using anything from a JSON file to a SQL Server or a secure storage mechanism for things like secrets.

Ideally we'd like using NFig to be as "native" as possible, so, our first task is to make sure settings provided by NFig can be resolved as `IOptions` implementations and to configure the backing store for NFig. We can do that in our application startup:

```c#
public enum Tier
{
    Local,
    Dev,
    Test,
    Prod,
}

public enum DataCentre
{
    Local,
    Timbuktu,    
}

public class Startup
{
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // add services needed by NFig
        // under the hood this configures the DI container
        // to resolve NFig settings as IOptions implementations
        services.AddNFig<Settings, Tier, DataCenter>();
    }

    public void Configure(IApplicationBuilder app, IHostingEnvironment env)
    {
        // configure NFig's backing store
        app
            .UseNFig<Settings, Tier, DataCenter>(
                (cfg, builder) =>
                {
                    var settings = cfg.Get<AppSettings>();
                    var connectionString = cfg.GetConnectionString("Redis");

                    // connects NFig to Redis
                    builder.UseRedis(settings.ApplicationName, settings.Tier, settings.DataCenter, connectionString);
                }
            );
    }
}
```

You'll notice that we've specified a concrete type for our settings (`Settings` from before) and also enum types for the `Tier` and `DataCentre`. The latter two allow our application to specify different settings per data centre and tier.

Now, instead of using a static reference to `Settings.FeatureFlags.EnableWhizzbang`, we can inject an `IOptions<FeatureFlagSettings>` into our consuming class:

```c#
public class HomeController : Controller
{
    private readonly FeatureFlagSettings _featureFlags;

    public HomeController(IOptions<FeatureFlagSettings> featureFlags)
    {
        _featureFlags = featureFlags.Value;
    }

    public IActionResult Home()
    {
        if (_featureFlags.EnableWhizzbang)
        {
            return Content("Whizzbang enabled!");
        }

        return Content("Whizzbang disabled");
    }
}
```

## Exposing NFig UI

We've also added middleware that handles the rendering of the NFig UI views. This is exposed as a static class that handles the HTTP request directly and can be called from an MVC controller or by being hooked up directly during application startup. This allows you to use whatever security model and routing mechanism yohr application is currently using:

**Using MVC**

```c#
public class SettingsController
{
    // restrict this route to users in the Admins role
    [Authorize(Roles = "Admins")]
    [Route("settings/{*pathInfo}")]
    public Task Settings() => NFigMiddleware<Settings, Tier, DataCenter>.HandleRequestAsync(HttpContext);
}
```

**Using Kestrel**

```c#
public class Startup
{
    // ... other startup bits here

    public void Configure(IAppBuilder app, IHostingEnvironment env)
    {
        app.MapWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/settings"),
            appBuilder => appBuilder.Run(NFigMiddleware<Settings, Tier, DataCenter>.HandleRequestAsync)
        );
    }
}
```

## Advanced Functionality

We can even go so far as composing our settings classes from other configuration sources. NFig doesn't currently support encryption of secrets (although it's being developed for v3) so it isn't a good idea to store passwords or other secret information within it. In .NET Core, however, we can use the Options framework to compose our settings classes from multiple places; we can fetch our secrets from somewhere secure, using .NET's configuration APIs. Here's an example using user secrets (note: user secrets are really intended for use at development time, this is not production-worthy code!):

**secrets.json**

```json
{
    "Database:Username": "user",
    "Database:Password": "pass123!"
}
```

**Program.cs**

```c#
public class Program
{
    public static void Main(string[] args)
    {
        CreateWebHostBuilder(args).Run();
    }

    public static IWebHostBuilder CreateWebHostBuilder(string[] args)
    {
        return WebHost.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(
                (hostingContext, config) =>
                {
                    config
                        .AddJsonFile("appSettings.json", optional: false, reloadOnChange: true)
                        .AddUserSecrets<Startup>();
                    }
            )
            .UseStartup<Startup>();
        }
}

public class Startup
{
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddNFig<Settings, Tier, DataCenter>();

        services
            .AddOptions<DatabaseSettings>()
            .Configure(
                s => 
                {
                    var databaseSettings = _configuration.GetSection("Database").Get<DatabaseSettings>();

                    s.Username = databaseSettings.Username;
                    s.Password = databaseSettings.Password;
                });
    }
}
```

## Some Implementation Details

You might be asking why we didn't implement .NET Core's `IConfigurationSource` and `IConfigurationProvider` interfaces and integrate at a lower level into the configuration subsystem... We explicitly decided not to do so because NFig manages the lifetime and persistence of settings itself. If we implemented the configuration interfaces we'd need to expose our configuration values as an `IDictionary<string, string>` and allow .NET Core to manage the binding of those values to strongly-typed classes. NFig already does the following for us:

 - it instantiates the settings classes and re-creates them whenever they change
 - it marshals configuration values from strings to their actual types and back again
 
In short, we really don't need to hook into the framework at such a low-level and hooking into `IOptions<T>` is sufficient for our needs. 

That said, we've had to make some adjustments to how the Options framework works by default by implementing some of the interfaces made available to us. 

There are two kinds of options that you can inject into your classes - `IOptions<T>` and `IOptionsSnapshot<T>`.

### `IOptions<T>`

These are registered as a singleton so the `Value` property always resolves to the value that is initially read from a configuration source.

### `IOptionsSnapshot<T>`

These are registered as a scoped dependency so the `Value` property is always the most current value and retains that value throughout the scope (e.g. an HTTP request).

By default these are both implemented by [`OptionsFactory<T>`](https://github.com/aspnet/Extensions/blob/master/src/Options/Options/src/OptionsFactory.cs) in .NET Core and as you can see from the code it tends to be a bit allocatey; an instance of `OptionsCache<T>` is created which contains a `ConcurrentDictionary<string, T>` containing a lazily initialized mapping of option names to their underlying objects. When using `IOptionsSnapshot<T>` that starts to add up because you get a new instance per request. We try to minimise allocations where possible here at Stack Overflow (GC hurts at scale!) so we override the default lifetime for NFig-provided settings to be a singleton that accesses the most recent value of a setting all the time by using the value tracked within an `IOptionsMonitor<T>`. This eliminates the allocations to be once per app per option type. To keep the options framework informed of when our NFig settings change we register a `IChangeTokenSource<T>` which notifies any listeners (including the `IOptionsMonitor<T>` mentioned above) of those changes.

## Summary
 
We've successfully integrated NFig into our .NET Core applications using this pattern and the library is available on [NuGet](https://www.nuget.org/packages/NFig.AspNetCore/). You can poke at the source code on [GitHub](https://github.com/NFig/NFig.AspNetCore). Have fun!

[NFig List]: /img/nfig-and-netcore-1.png "NFig List View"
[NFig Setting]: /img/nfig-and-netcore-2.png "NFig Setting View"
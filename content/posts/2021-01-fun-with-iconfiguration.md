---
title: "Fun with IConfiguration"
date: 2021-01-19T15:00:00Z
tags: [.net-core, configuration, stack-overflow, azure, keyvault, appconfig]
images: [img/iconfiguration-cover.png]
---

These days a .NET application is typically configured at startup using an extensible API known as thr [configuration builder](https://docs.microsoft.com/en-us/dotnet/core/extensions/configuration-providers) API. This allows the use of arbitrary sources of configuration - typically, out of the box, that means environment variables, command line args and JSON files (e.g. `appsettings.json`) but can also mean more "exotic" sources; anything from INI files to a SQL database to a secure secret store.

## How it works

When defining an application's configuration sources the individual providers are added using a fluent syntax. Here's a simple example of defining a few configuration providers in an application's `Program.cs`:


```c#
public class Program
{
    public static void Main(string[] args)
    {
        WebHost.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(
                (hostingContext, config) =>
                {
                    config
                        .AddJsonFile("appsettings.json")
                        .AddEnvironmentVariables()
                        .AddCommandLine();
                }
            )
            .UseStartup<Startup>()
            .Build()
            .Run();
    }
}
```

At its most basic a configuration provider is expected to provide access to the values stored within it by key. Keys can contain zero or more colons which are used to indicate nesting. Let's take the JSON provider as an example (`appsettings.json` in our code snippet above):

```json
{
    "EnableFeature": true,
    "ConnectionStrings": {
        "SQL": "Data Source=.;Initial Catalog=MyDatabase;Integrated Authentication=SSPI"
    },
    "Kestrel": {
        "Endpoints": {
            "Http": {
                "Url": "http://+/"
            },
            "Https": {
                "Url": "https://+/"
            }
        }
    } 
}
```

When this is parsed by the JSON provider it results as a set of keys as follows:

```
EnableFeature=true
ConnectionStrings:SQL=Data Source=.;Initial Catalog=MyDatabase;Integrated Authentication=SSPI
Kestrel:Endpoints:Http:Url=http://+/
Kestrel:Endpoints:Https:Url=https://+/
```

Here we can see that each level of nesting in the JSON is delimited by a colon and each delimited part is the key of that section.

Providers added later can override keys defined in those defined earlier.  Using our code snippet above - a key of `EnableFeature` in the `appsettings.json` file is overridden by an `ENABLEFEATURE` environment variable or `--enablefeature` command line argument.

## Configuration in Stack Overflow

In Stack Overflow we use the following order of precedence when defining the providers used by the application:

 1. `appsettings.json`
 1. `appsettings.{environment}.json`
 1. `environmentsettings.{environment}.json`
 1. Environment variable
 1. Command line args
 
`appsettings.json` is generally configured with barebones defaults, and mostly empty values. Some of those  are then overridden with actual values in `appsettings.{environment}.json`. These are either "Local" configuration used for local development, or configurations written at deployment time for non-local environments. Pretty standard stuff! 

Where things get a little bit quirky is with `environmentsettings.{environment}.json`. For that we need a little bit of history...

### SiteSettings

Stack Overflow has had its own multi-tenant settings infrastructure for a long time. It works using some of the same principles as `IConfiguration` - it's key/value-based at the storage layer, but it uses a strongly-typed set of classes in C# to provide type information. Here's how that looks:

```c#
public class SiteSettings
{
    public MyFeatureSettings MyFeature { get; }
    
    public class MyFeatureSettings
    {
        [SiteSetting]
        [DefaultValue(true)]
        public bool Enabled { get; }
        
        [SiteSetting]
        [DefaultValue("0.00:00:05")]
        [EnvironmentDefaultValue(Tier.Local, "0.00:00:30")]
        public TimeSpan Timeout { get; }
}
```

This shows us defining a settings group called `MyFeature` that has two properties `Enabled` and `Timeout`. On each property we've provided attributes that define the defaults for those properties and also an environmental default that specifies the value used during `Local` development.

That's fairly straightforward - settings are exposed as a set of global defaults and also on each site in the Stack Exchange Network, giving us our multi-tenancy support. We have a UI exposed to developers that allows us to override defaults specified in code either globally or for a specific site - these overrides are stored in the database. This lets us do things like configure a feature network-wide or just on individual sites on the network.

In the underlying SQL storage the values are stored "stringly-typed" and the code responsible for reading / writing them converts between the property type at runtime.

Site settings have a load more features, but in the interest of brevity, I will skip over those details for now. Marc Gravell wrote pretty much every last line of this originally and is by far the subject matter expert here, so I'll leave that for him perhaps :)!

### Environment-specific settings

Recently I've been doing some work with Azure and, as a result, we've started to think about how we want to retrieve and manage our configuration in such an environment.

Whilst analyzing how site settings have been used within the codebase over the years we found that we had inadvertently conflated things that belonged in settings (whether feature X was enabled and its associated settings) with things that don't (environment-specific infrastructure - e.g. service endpoints, connection strings, secrets). Some of the latter _make sense_ to live in the site settings hierarchy - these settings are readily accessible in all the places that need them - but specifying those defaults in code turned out to be short-sighted.

This situation came about because, overall, our infrastructure is fairly static. Occasionally we add a server here and there, but we know that we have our dev and prod environments and that they more or less look the same - hence specifying a bunch of (overrideable) defaults in code is a safe(ish) assumption! However, once we start deploying things to more dynamic environments like containers or ephemeral environments spun up in Azure those assumptions no longer hold true!

To solve this problem we decided to allow site settings to load their values using the following precedence:

 1. code-based defaults (global)
 1. application's `IConfiguration` (global)
 1. DB overrides (global or site-specific)
 
Here we're adding #2 as a new source of default values - but a source that has lots of flexibility! By plugging into `IConfiguration` we can provide deployment environment-specific defaults without needing site settings to "know" where those values came from.

How does this help us in Azure? Well, we can trivially configure our application to use Azure's AppConfig & KeyVault services and our environment-specific defaults can now be fed directly from the environment the application is hosted in!

However, in the absence of equivalent services in the data centre (which we _will_ provision eventually - things like Vault for secrets and Consul for service discovery are good options here) we are stuck with using `environmentsettings.{environment}.json` as a way to specify the data centre environment :(

## Using KeyVault & AppConfig

Our use of both KeyVault & AppConfig is pretty simple. When an environment is provisioned in Azure using Terraform some global secrets such as SQL admin credentials and credentials to third party systems are written into KeyVault. Once that process has completed we run a bootstrap script that consumes some of those secrets and provisions our SQL accounts, shared secrets and introspects the environment to configure service endpoints. Any key/value pairs are persisted into KeyVault or AppConfig depending on the kind of value it is (secret/service metadata/etc).

In the application we configure AppConfig & KeyVault providers using [managed identity](https://docs.microsoft.com/en-us/azure/active-directory/managed-identities-azure-resources/overview) (hooray, no secrets in JSON!) which means we can consume `IConfiguration` in our site settings loader and dependent settings are configured appropriately.

However, we quickly discovered that having individual settings, potentially composed of other settings, becomes somewhat unmaintainable. Consider a connection string to SQL:

> `Server=sql.database.windows.net;Database=MyDatabase;User ID=myapp-readwrite;Password=Password123!`

This contains the hostname of a server and the credentials used to connect to it. These are all discrete pieces of information, with their own, potentially disconnected, lifetimes, that are composed to form the whole connection string. That means that any time one of those pieces of information changes we must reconstruct all dependent values from its constituent pieces again. Eurgh!

But, thankfully, there's a simple solution!

### Substitution

If we change the configuration values composed from other values to use placeholders then we can change our connection string to:

> `Server=${SqlServer};Database=MyDatabase;User ID=${SqlUser};Password=${SqlPassword}`

But this doesn't work out of the box! To make this happen we've invented a configuration provider that wraps other providers and allows them to use  values from elsewhere in the configuration system. We can do this by including the `StackExchange.Utils.Configuration` package and configuring our application as follows:

```c#
public class Program
{
    public static void Main(string[] args)
    {
        WebHost.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(
                (hostingContext, config) =>
                {
                    config
                        // environmental defaults - service endpoints, servers
                        .AddJsonFile("globals.json")
                        // environmental secrets - API keys, passwords
                        .AddJsonFile("secrets.json")
                        .AddEnvironmentVariables()
                        .AddCommandLine()
                        .WithSubstitution(
                            c =>
                            {
                                c.AddJsonFile("appsettings.json")
                                 .AddEnvironmentVariables()
                                 .AddCommandLine();
                            }
                        );
                }
            )
            .UseStartup<Startup>()
            .Build()
            .Run();
    }
}
```

This is a little more complex, so let's break it down. First we use a couple of JSON files - one called `globals.json` and another called `secrets.json`. `globals.json` contains anything that pertains to the global environment that the application is running as - service endpoints mostly. `secrets.json` contains secrets for environment - i.e. credentials. Both of these would typically use other services in a production environment - for us, in Azure, that'd be AppConfig and KeyVault.

We then allow those sources to be overridden by environment variables and command line args. Our JSON files would look something like this:

```json
// globals.json
{
    "SqlServer": "sql.database.windows.net",
}

// secrets.json
{
    "SqlUser": "myapp-readwrite";
    "SqlPassword": "Password123!"
}
```

Next we use our new extension `WithSubstitution`. This expects a delegate that is used to configure a child configuration builder. Anything added to this builder will automatically have any placeholders of the form `${key}` replaced with the value obtained from any configuration sources in the _parent_ scope. We've added `appsettings.json` with overrides from environment variables and command line args. If we use the following `appsettings.json`:

```json
{
    "ConnectionStrings": {
        "SQL": "Server=${SqlServer};Database=MyDatabase;User ID=${SqlUser};Password=${SqlPassword}"
    }
}
```

At runtime, the placeholders are replaced, so fetching `ConnectionStrings:SQL` from an `IConfiguration` will result in a fully formed connection string. Yay!

If the individual components change _and_ the underlying configuration source(s) supports change tokens then those changes are propagated to any dependencies.

### Namespacing

Substitution is very useful, but we also found that it is useful to separate out things like secrets. This prevents the application accidentally consuming a secret from an insecure configuration source. To do this we decided to implement a simple prefixing provider that we can use as follows:

```c#
public class Program
{
    public static void Main(string[] args)
    {
        WebHost.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(
                (hostingContext, config) =>
                {
                    config
                        .WithPrefix(
                            "globals",
                            c => 
                            {
                                // environmental defaults - service endpoints, servers
                                c.AddJsonFile("globals.json")
                                 .AddEnvironmentVariables()
                                 .AddCommandLine();
                            }
                        )
                        .WithPrefix(
                            "secrets",
                            c =>
                            {
                                // environmental secrets - API keys, passwords
                                c.AddJsonFile("secrets.json")
                                 .AddEnvironmentVariables()
                                 .AddCommandLine();
                            }
                        )
                        .WithSubstitution(
                            c =>
                            {
                                c.AddJsonFile("appsettings.json")
                                 .AddEnvironmentVariables()
                                 .AddCommandLine();
                            }
                        );
                }
            )
            .UseStartup<Startup>()
            .Build()
            .Run();
    }
}
```

This code configures our `global.json` source (with any env and command line overrides) with the prefix `globals` and `secrets.json` with `secrets`. Our connection string in `appsettings.json` now becomes:

> `Server=${globals:SqlServer};Database=MyDatabase;User ID=${secrets:   SqlUser};Password=${secrets:SqlPassword}`

It is now abundantly obvious that `SqlUser` and `SqlPassword` should be sourced from a secret store and that service endpoints come from the global environment.

## Wrapping Up

We've found these approaches to suit the way we want to handle configuration in our .NET applications  and it greatly simplfies the management of configuration in more dynamic environments. We can trivially change settings in AppConfig or KeyVault and they propagate to running applications without a reboot.

We've packaged the substitution and prefixing functionality into a NuGet package called `StackExchange.Utils.Configuration` that can be consumed in any .NET Core 3.1 or above application. Links are below, we hope you find it as useful as we have!

GitHub: https://github.com/StackExchange/StackExchange.Utils
NuGet: https://www.nuget.org/packages/StackExchange.Utils.Configuration/

---
title: "Authenticating to Google using PowerShell and OAuth"
date: 2020-01-14T15:00:00Z
tags: [powershell, .net-core, stack-overflow, google, oauth]
images: [img/dev-local-setup-cover.png]
---

At Stack we've written and maintain a set of scripts named "Dev-Local-Setup" that our developers and designers can use to quickly provision their local environments. They assist in installing pre-requisites (things like Chocolatey, SQL Server, VS 2019, .NET Core SDKs), pulling down repos and provisioning databases and websites used by specific teams. They're packaged as a PowerShell module and used by a menu-driven CLI interface that makes it easy to perform those tasks quickly and efficiently.

<img src="/img/dev-local-setup-1.png" width=640 alt="Dev-Local-Setup"><br/>
<sub style="color:lightgray">Dev-Local-Setup</sub>

We have a bunch of shared testing data that we store on a file share accessible over the VPN and we also synchronize to Google Drive to facilitate quick downloads for an audience that is spread all over the globe (downloading things from an NY-based data center can be sloooow when you're in Australia!).

Google Drive requires authentication in order to allow things to be downloaded so we have configured our scripts as an application in Google and use OAuth to obtain an access token whenever somebody needs to download resources stored there.

If you search around the web you'll find plenty of "solutions" to this issue, but most of them want you to obtain an access token and embed it in your script. I cannot stress what a _terrible_ idea this is - never store secrets in source control, not to mention that the access token is tied to whoever writes the script. It's probably worth mentioning that if you need to fully automate script access to Google APIs I'd suggest looking into service accounts and passing the relevant credentials from the environment that runs the script instead. Our approach is useful for when the script is used interactively.

Here's how we used to do things:

 - Import the WinForms types into our PowerShell script
 - Construct a WinForms `Form` and embed a `WebBrowser` object in it
 - Hook the `ContentLoaded` event on the `WebBrowser` to detect URL changes
 - Navigate to the URI used to obtain an authorization token once a user has successfully authenticated.
 - Configure the redirect URI to be some surrogate that we can intercept using the `DOMLoaded` event
 - When we hit the surrogate URI we extract the `code` querystring parameter and use it to obtain an access token
 - Use the access token to download the resources we need!

 That has worked fine for a long time, but lately IE11 support has started to disappear across the web (including from [Stack Exchange](https://meta.stackexchange.com/a/337986/267572)) and the Google authentication flows don't work reliably in the embedded frame. Our first attempt to fix this was to use Microsoft's [`WebView`](https://docs.microsoft.com/en-us/windows/communitytoolkit/controls/wpf-winforms/webview) component - this is an equivalent Winforms control that embeds Edge instead of IE11. Easy peasy, let's do it!

<img src="/img/dev-local-setup-2.jpg" width=250 alt="Ruh roh"><br/>

Turns out it isn't that easy! Given the nature of the scripts - installing software and other privileged operations - we need to run in an elevated security context. This is our first roadblock - `WebView` has a real [hard time](https://github.com/windows-toolkit/Microsoft.Toolkit.Win32/issues/13) doing anything when the security context is not that of a regular user. It appears that this is because some core infrastructure pieces of UWP applications (which Edge is) cannot run in an elevated security context.

If we run our scripts as a regular user we can get a modal to render the Edge-based browser but when we come to redirect to our surrogate URI (in our case it happens to be `https://localhost/`) the browser won't render anything. Eurgh, another roadblock! In this case UWP applications have additional security restrictions on accessing localhost in order to prevent port scanning - a common technique used by malware to determine whether a service is running or not. We can overcome this roadblock by running `checknetisolation LoopbackExempt -a -n=""Microsoft.MicrosoftEdge_8wekyb3d8bbwe"` but that doesn't feel like a good thing to do from scripts.

At this point we took a step back and re-considered our approach. The above roadblocks might well be solved by the new [`WebView2`](https://docs.microsoft.com/en-us/microsoft-edge/hosting/webview2) component that embeds the preview version of Edge based upon Chromium, but it's still in beta and requires us to install additional SDKs to work correctly.

We decided to take another approach - here's the crazy we decided upon...

### Create a Web Server

Create a bare-bones .NET Core 3.0 Kestrel-based application - a `.csproj` and a `.cs` contains everything we need to intercept a request to a surrogate URI. When we receive a valid request with an authorization code we output it to stdout and exit. If we receive an error we just exit. That looks a little like this:

**OAuthListener.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>netcoreapp3.0</TargetFramework>
    <NoDefaultLaunchSettingsFile>true</NoDefaultLaunchSettingsFile>
  </PropertyGroup>

</Project>
```

**Program.cs**
```cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OAuthListener
{
  class Program
  {
    static readonly CancellationTokenSource _cts = new CancellationTokenSource();

    static Task Main(string[] args) =>
      WebHost.CreateDefaultBuilder()
        // prevent status messages being written to stdout
        .SuppressStatusMessages(true)
        // disable all logging to stdout
        .ConfigureLogging(config => config.ClearProviders())
        // listen on the port passed on the args
        .UseKestrel(options => options.ListenLocalhost(int.Parse(args[0])))
        .Configure(app => app.Run(async context =>
          {
            var message = "ERROR! Unable to retrieve authorization code.";
            if (context.Request.Query.TryGetValue("code", out var code))
            {
              // we received an authorization code, output it to stdout
              Console.WriteLine(code);
              message = "Done!";
            }

            await context.Response.WriteAsync(message + " Check Dev-Local-Setup to continue");

            // cancel the cancellation token so the server stops
            _cts.Cancel();
          })
        )
        .Build()
        // run asynchronously using the cancellation token
        // to signal when the process should end. This will be awaited
        // by the framework and the process will end when the cancellation
        // token is signalled.
        .RunAsync(_cts.Token);
    }
}
```

As part of our pre-requisites we install .NET Core 3.0 SDK so to execute this we simply `dotnet run` the project passing the port number we want to listen to.

### Open the Default Web Browser

Instead of opening a browser embedded in a Winforms modal we can simply use the user's default browser. This has a couple of advantages - we don't need to worry about elevation affecting things (this is effectively just performing a `Shell.Open` call which executes in the same privilege level as Windows Explorer) and often the developer is already authenticated to Google in that browser so it's less work overall.

When we construct the URL we configure our redirect URL to point at `http://localhost:<port>` where `<port>` is an arbitrary port number specified by our script in the next step...

### Using from PowerShell

Finally we hook it all together...

```powershell
function Get-GoogleAccessToken {
  Param(
    [string][Parameter(Position = 0, Mandatory = $true)] $Scope
  )

  $accessToken = $null;
  # arbitrary port number to listen on
  $port = 12345
  # client identifier of your application configured in the Google Console
  $clientId = "<your client id>"
  # client secret of your application configured in the Google Console
  $clientSecret = "<your client secret>"
  # URL used to obtain start an OAuth authorization flow
  $url = "https://accounts.google.com/o/oauth2/v2/auth?client_id=$clientId&redirect_uri=http://localhost:$port&response_type=code&scope=$Scope"

  # Kick off the default web browser
  Write-Host "Launching web browser to authenticate to GDrive..."
  Start-Process $url

  # Spin up our .NET Core 3.0 application hostig the web server
  $authorizationCode = & dotnet run -p .\OAuthListener -- $port

  # if an authorization code was written to stdout then
  # exchange it for an access token, otherwise output an error
  if ($authorizationCode) {
    $authorizationResponse = Invoke-RestMethod -Uri "https://www.googleapis.com/oauth2/v4/token?code=$authorizationCode&client_id=$clientId&client_secret=$clientSecret&redirect_uri=http://localhost:$port&grant_type=authorization_code" -Method Post
    $accessToken = $authorizationResponse.access_token
  }
  else {
      Write-Host "Unexpected error while retrieving access token" -ForegroundColor Red 
  }

  return $accessToken;
}
```

Then to retrieve an access token and call an API using it (in our case to download a file from Google Drive):

```powershell
# Grab the access token
$accessToken = Get-GoogleAccessToken -Scope https://www.googleapis.com/auth/drive.readonly
# Use it in an authorization header
$headers = @{
    Authorization = "Bearer $($accessToken)"
}
# Download the file from Google Drive
$destFile = Join-Path $env:TEMP $fileId
Invoke-WebRequest -Uri "https://www.googleapis.com/drive/v3/files/$($fileId)?alt=media" -Destination $destFile -Headers $headers
```

And that's it! This approach seems to be a lot more reliable than our previous approach; we get to use a modern browser that doesn't have issues handling Google's OAuth flow and a simple web server that is trivial to understand...
# About
**ssoweb** is a Star Stable Online game multi-account manager that lets you seamlessly switch between your accounts without having to login to them every time.  
Especially useful if you're looking to avoid 2-factor authentication!  

<img width="952" height="642" alt="Screenshot From 2026-06-12 02-10-25" src="https://github.com/user-attachments/assets/b022eea3-26b1-45e6-936c-988513dc58ae" />
<img width="952" height="642" alt="Screenshot From 2026-06-12 01-56-53" src="https://github.com/user-attachments/assets/9d176145-ed0b-430c-bf4e-09284a824e56" />

It consists of:  
**ssoweb** - user-friendly graphical interface (NodeJs electron [Html/Css/Jquery])  
**ssorecheck** - cli utility for fetching in-game account data and proxies (C#)  
**patch** - patched game-client asar with custom functions (NodeJs)  

Features:  
Cross-platform (Windows and Linux)  
Supports sandboxing through Bottles (Flatpak) to isolate the game from the rest of your valuable system data  
Skip having to re-log into the accounts by keeping track of session tokens and device ids associated  
Automatically applies game-client patches when the game updates  
Fetches proxies for you in case you need to re-check certain accounts  

#### So how does it work?
As this was a hobby project, initial setup might be a bit complicated (all steps below), but once you're through that it's pretty easy to use.  
Simply put, the application keeps track of unique session tokens and device IDs for each of the accounts imported.  
Each time you open SSO game client it generates a new unique session token, which is then read and saved inside `data.json` for future use, then when you want to play on that account again, it's automatically injected into the client, game-client patch then sets the device Id parameter to match the one used to initially log into the account, skipping the whole login process.   

After the initial setup, the app automatically patches the client for you.  
Occasionally, you might wanna re-fetch account information, all you have to do is select the account in GUI and click the `Recheck` button; From there on, ssorecheck (automatically) fetches proxies for you and attempts to get latest in-game account data.  
In the case of fetching latest account data, ssorecheck generates a unique session token in the same fashion that the official game-client itself does.  

#### Do I need to give it my account credentials?
Not necessarily.  
We'll later cover which JSON fields are required when importing an account, but for now, you can choose between the two combinations:  
1) email/password - full functionality
2) session token/device id - only handles keeping track of said values across plays on that account  

Either of the 2 ways of using it could technically give someone the ability to use the account.  

> [!IMPORTANT]
> In case the game introduces larger update, it might break patched version from working.
> If that happens, you might have to manually apply patch only to specific files ssoweb depends on, such as `deviceId.js` module.

## Build and setup
First of all, we have to apply custom game-client patch for session injecting to work.  
Note down the latest version of the game (2.57.1 as of writing this page).  
If you want to be sure, get it from [official updater api](https://launcher-release-prod.starstable.com/latest.yml) by opening said YAML file in any text editor.  

Change `version` JSON field of `.../patch/sso/package.json` to match the latest official one.

Pack up the patched game-client with:
```console
asar pack patch/sso/ app.asar
```
Make sure your terminal's working directory is set to where `patch` and `ssorecheck-src` directories are.  

From there on, simply replace the existing `C:\Program Files\Star Stable Online\release\app.asar` with the one you just packed up inside `ssoweb/app.asar`.  

### ssorecheck
By default, ssorecheck fetches US proxies to avoid using your own IP address to fetch account data and such.  

> [!WARNING]
> You'll wanna change `IPinfoAPIKey` to your own one inside `GetCountry()` function in `Program.cs`.
> You can get it for free from said IP data provider or re-write the function to your own liking.

Build it:
```console
dotnet build
```

That should give you `linux` and `win-x64` versions of it, place them inside `ssorecheck` directory where your `ssoweb` executable is.  
So the structure should be:  
ssoweb  
|-> ssoweb(.exe)  
|-> ssorecheck -> linux  
|-> ssorecheck -> win-x64  

In case your account is not US-based, you'll want to either make proxy fetching function within ssorecheck get proxies from desired country or just disable proxies altogether:
```csharp
if (p.Alive())
{
    p.GetCountry();

    if (p.GeoLocation.Country == "US") // Replace US with your desired country
    {
        ...
    }
}
```
```csharp
var Handler = new HttpClientHandler
{
    Proxy = new WebProxy(Direct),
    UseProxy = false, // This is 'true' by default
};
```
Then build the project with `dotnet build`.  

### ssoweb
This is pretty straight forward unless you're planning to use it on Linux inside Bottles (Flatpak).  

Build it:
```console
npm install
npm run dist:all
```

> [!NOTE]
> If you're on Linux, inside `ssoweb/main.js` find `getConfig()` function and change `bottleName` property to the name of the Bottle where your Star Stable Online game is installed.

You should now be able to see `ssoweb/dist/` directory where you'll find app executables.  
That's it!  

## Account importing format
Once you press '+' button inside the ssoweb GUI app, it'll ask you to paste your account(s).  
The program expects them in the following JSON format:  
```json
{
    "user": "example@email.com:password", (string)
    "verified": true, (boolean)
    "banned": false, (boolean)
    "coins": 0, (integer)
    "premium": false, (boolean)
    "server": "Wind Star (sso-na-ec-01 : 2/26)", (string)
    "session-token": "redacted", (string)
    "device-id": "redacted" (string)
}
```
It can be a single account object or an array of them.  

`session-token` and `device-id` fields are required in case you haven't provided it valid `user` field.  
Otherwise all fields except `user` are optional - just hit recheck once you import the account.  

## Some notes
While the program doesn't mess with the game files themselves (like cheats for an example), it most literally patches the game-client, so it may or may not be bannable.  
Use at your own risk, this was a learning experience at best for me - game's kinda fun though!  

You'll need dotnet-sdk, asar, nodejs to build (and use) this program.  
Tested on Arch Linux & Windows 11.  

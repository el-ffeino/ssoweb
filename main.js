const { app, BrowserWindow, ipcMain, session } = require('electron');
const { spawn } = require('child_process');
const path = require('path');
const os = require('os');
const fs = require('fs');
const { EventEmitter } = require('stream');
const { unsubscribe } = require('diagnostics_channel');
const { ProxyAgent, request } = require('undici');
const asar = require('@electron/asar');
const crypto = require('crypto');

/*try {
  require('electron-reloader')(module, { debug: true, watchRenderer: true });
} catch {}*/

let mainWindow;
let gameWindow;

let proxy = null;
let proxyAgent = null;
let retries = 20;

const config = getConfig();
let state = { 
  clientVersion: '2.56.1',
  lastProxyFetch: 0
};

const client = {
  message(status, message) {
    if (mainWindow) {
      mainWindow.webContents.send('message', { status, message });
    } else {
      console.log(`[${status}] ${message}`);
    }
  }
};

const SSO = {
  API: {
    clientUpdaterURL: 'https://launcher-release-prod.starstable.com/latest.yml',
    sessionInit: 'https://lb-pub.prod.starstable.com/api-gateway/1.0/session/create'
  },
  userAgent() {
    return `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) StarStableOnline/${state.clientVersion} Chrome/128.0.6613.186 Electron/32.2.7 Safari/537.36`;
  },
  async getClientVersion() {
    try {
      const { statusCode, body } = await request(this.API.clientUpdaterURL, {
        headers: { 'User-Agent': this.userAgent() }
      });

      if (statusCode !== 200) return null;

      const data = await body.text();
      const version = data.match(/version:\s*([^\s]+)/i);
      return version ? version[1].trim() : null;
    } catch {
      return null;
    }
  },
  unpackAsar()
  {
    try {
      fs.mkdirSync(config.sso.asar.unpacked, { recursive: true });
      asar.extractAll(config.sso.asar.packed, config.sso.asar.unpacked);
    } catch (error) {
      client.message('error', `Failed to unpack asar: ${error}`);
    }
  },
  async packAsar()
  {
    try {
      const patched = path.join(config.sso.resources, 'temp.asar');
      await asar.createPackage(config.sso.asar.unpacked, patched);

      fs.rmSync(config.sso.asar.packed, { force: true });
      fs.renameSync(patched, config.sso.asar.packed);
      
      fs.rmSync(config.sso.asar.unpacked, { recursive: true, force: true });
    } catch (error) {
      client.message('error', `Failed to patch asar: ${error}`);
    }
  },
  patchClientVersion()
  {
    if (!fs.existsSync(config.sso.asar.unpacked)) {
      client.message('warn', `Function 'SSO.patchClientVersion()' was called without 'SSO.unpackAsar()'..`);
      this.unpackAsar();
    }

    let packageFile = path.join(config.sso.asar.unpacked, 'package.json');
    try {
      const content = fs.readFileSync(packageFile).trim();
      let package = JSON.parse(content);

      package.version = state.clientVersion;
      const patched = JSON.stringify(package, null, 2);

      try {
        fs.writeFileSync(packageFile, patched, 'utf-8');
      } catch (error) {
        client.message('error', `Unable to write to '${packageFile}': ${error}`);
      }
    } catch (error) {
      client.message('error', `Failed to patch 'package.json': ${error}`);
    }
  }
};

async function initialize()
{
  if (!fs.existsSync('state.json')) {
    fs.writeFileSync('state.json', JSON.stringify(state, null, 2), 'utf-8');
  }
  try {
    const data = fs.readFileSync('state.json', 'utf-8').trim();
    state = JSON.parse(data);
  } catch {}
  
  let latestClientVersion = await SSO.getClientVersion();

  if (latestClientVersion && state.clientVersion != latestClientVersion) {
    state.clientVersion = latestClientVersion;

    if (fs.existsSync(config.ssorecheck.data)) {
      try {
        const data = fs.readFileSync(config.ssorecheck.data, 'utf-8').trim();
        let cdata = JSON.parse(data);
        cdata.ClientVersion = latestClientVersion;
        fs.writeFileSync(config.ssorecheck.data, JSON.stringify(cdata, null, 2), 'utf-8');
      } catch {}
    }

    saveState();
  }

  let currentUnixTime = Math.floor(Date.now() / 1000);
  const secondsInDay = 86400;
  if (currentUnixTime - state.lastProxyFetch > secondsInDay) {
    client.message('success', 'Fetching latest proxies..');
    getProxies();
  }
}

function saveState() { fs.writeFileSync('state.json', JSON.stringify(state, null, 2), 'utf-8'); }

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 920,
    height: 600,
    frame: false,
    resizable: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true
    }
  });

  mainWindow.loadFile('index.html');
  // mainWindow.webContents.openDevTools(); // uncomment if needed
}

app.whenReady().then(async () => {
  await initialize();
  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

ipcMain.on('minimize-app', () => mainWindow.minimize());
ipcMain.on('close-app', () => mainWindow.close());

ipcMain.handle('load-data', async () => 
{
  const isPackaged = app.isPackaged;
  const basePath = isPackaged ? path.dirname(process.execPath) : __dirname;

  const filePath = path.join(basePath, 'data.json');

  try 
  {
    if (!fs.existsSync(filePath)) {
      fs.writeFileSync(filePath, '[]', 'utf-8');
      return [];
    }

    const content = fs.readFileSync(filePath, 'utf-8');

    try
    {
      return JSON.parse(content);
    }
    catch (error)
    {
      console.error(`Invalid JSON array provided in 'data.json': ${error}`);
      return null;
    }
  } 
  catch (err) 
  {
    return { error: err.message };
  }
});

ipcMain.handle('save-data', async (event, content) => 
{
  const isPackaged = app.isPackaged;
  const basePath = isPackaged ? path.dirname(process.execPath) : __dirname;
  const filePath = path.join(basePath, 'data.json');

  try {
    const dataToWrite = JSON.stringify(content, null, 2);

    fs.writeFileSync(filePath, dataToWrite, 'utf-8');

    return { 
      success: true, 
      filePath,
      message: `Successfully saved to ${filePath}`,
      size: dataToWrite.length
    };
  } catch (err) {
    console.error('Save error:', err);
    return { error: err.message };
  }
});

ipcMain.handle('refreshAccount', async (event, account) =>
{
  return new Promise((resolve) =>
  {
    const args = ['recheck', account];
    const child = spawn(config.ssorecheck.exe, args,
    {
      cwd: config.ssorecheck.dir,
      stdio: ['ignore', 'pipe', 'pipe']
    });

    child.stdout.on('data', (data) =>
    {
      const raw = data.toString();
      console.log(raw);

      try
      {
        const msg = JSON.parse(raw.trim());

        if (msg.status === 'rechecked') {
          mainWindow.webContents.send('account-refreshed', {
            user: msg.message.user,
            sessionToken: msg.message.session_token,
            deviceId: msg.message.deviceId,
            coins: msg.message.starCoins,
            server: msg.message.server,
            verified: msg.message.verified,
            banned: msg.message.banned
          });

          if (msg.status === 'error' || msg.status === 'warn') {
            client.message(msg.status, msg.message);
          }

          if (msg.status === 'invalid_credentials') {
            client.message('error', msg.message);
            mainWindow.webContents.send('invalid-credentials', { user: account });
          }

          child.kill();
          resolve({ success: true });
          return;
        }
      }
      catch {}
    });
  });
});

ipcMain.handle('play', async (event, account) =>
{
  const sessionToken = account['session-token'];
  const deviceId = account['device-id'];
  
  try {
    fs.writeFileSync(config.sso.deviceId, deviceId, 'utf-8');
    fs.writeFileSync(config.sso.sessionToken, sessionToken, 'utf-8');
  } catch (error) {
    return { success: false, error: error };
  }

  const onSessionUpdated = (newToken) => {
    mainWindow.webContents.send('session-updated', {
      success: true, 
      user: account.user, 
      sessionToken: newToken 
    });
  };

  tokenWatcher.start();
  tokenWatcher.subscribe(onSessionUpdated);

  try
  {
    gameWindow = spawn(config.sso.executable, config.sso.executableArgs, 
    {
      detached: true,
      stdio: 'pipe'
    });
  } catch (error) {
    tokenWatcher.stop();
    tokenWatcher.unsubscribe(onSessionUpdated);
    console.error(`gameWindow failed to launch: ${error}`);
    return { success: false, error: 'Failed to launch game' };
  }

  gameWindow.stdout.on('data', (data) => { console.log(`gameWindow: ${data}`) });
  gameWindow.stderr.on('data', (data) => { console.error(`gameWindow: ${data}`) });
  gameWindow.on('error', () => { 
    tokenWatcher.stop();
    tokenWatcher.unsubscribe(onSessionUpdated);
  });
  gameWindow.on('exit', () => {
    tokenWatcher.stop(); 
    tokenWatcher.unsubscribe(onSessionUpdated);
  });
  
  return { success: true };
});

let tokenWatcher = {
  debounceTimer: null,
  watcher: null,
  emitter: new EventEmitter(),
  start()
  {
    this.stop();
    this.watcher = fs.watch(config.sso.sessionToken, (e, file) =>
    {
      if (e === 'change')
      {
        clearTimeout(this.debounceTimer);
        this.debounceTimer = setTimeout(() =>
        {
          try {
            const sessionToken = fs.readFileSync(config.sso.sessionToken, 'utf8').trim();
            this.emitter.emit('session-updated', sessionToken);
          } catch (error) {
            if (error.code !== 'ENOENT') {
              console.error(`Error reading new token: ${error}`);
            }
          }
        }, 150);
      }
    });
    this.watcher.on('error', (error) => {
      console.error(`Watcher error: ${error}`);
    });
  },
  stop()
  {
    if (this.watcher) {
      this.watcher.close();
      this.watcher = null;
    }
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
  },
  subscribe(callback) {
    this.emitter.on('session-updated', callback);
  },
  unsubscribe(callback) {
    this.emitter.off('session-updated', callback);
  }
};

function getConfig()
{
  let isPackaged = app.isPackaged;
  let Config = 
  {
    basePath: isPackaged ? path.dirname(process.execPath) : __dirname,
    platform: process.platform,
    windows: process.platform === 'win32',
    linux: process.platform === 'linux',
    bottleName: 'isolated',
    username: os.userInfo().username,
    home: os.homedir()
  }

  const bottle = path.join(Config.home, '.var', 'app', 'com.usebottles.bottles', 'data', 'bottles', 'bottles', Config.bottleName, 'drive_c');
  const programFiles = Config.windows? process.env.ProgramFiles : path.join(bottle, 'Program Files');
  const appData = Config.windows? process.env.APPDATA : path.join(bottle, 'users', Config.username, 'AppData', 'Roaming');

  const game = 'Star Stable Online';
  const flatpakArgs = ['run', '--command=bottles-cli', 'com.usebottles.bottles', 'run', '-p', game, '-b', 'isolated', '--', '%u'];

  const ssorecheckDir = Config.windows? path.join(Config.basePath, 'ssorecheck', 'win-x64') : path.join(Config.basePath, 'ssorecheck', 'linux');
  const ssorecheckData = path.join(Config.basePath, 'ssorecheck', 'cdata.json');
  const ssorecheckExe = Config.windows? path.join(ssorecheckDir, 'ssorecheck.exe') : path.join(ssorecheckDir, 'ssorecheck');

  Config = {
    ...Config,
    dir:
    {
      bottle: bottle,
      programFiles: programFiles,
      appData: appData
    },
    sso:
    {
      game: game,
      path: path.join(programFiles, game),
      executable: Config.windows? path.join(programFiles, game, `${game}.exe`) : 'flatpak',
      executableArgs: Config.windows? [] : flatpakArgs,
      resources: path.join(programFiles, game, 'resources'),
      asar: {
        packed: path.join(programFiles, game, 'resources', 'app.asar'),
        unpacked: path.join(programFiles, game, 'resources', 'sso_unpacked'),
      },
      deviceId: path.join(programFiles, game, 'resources', 'deviceid.txt'),
      sessionToken: path.join(appData, game, 'sso')
    },
    ssorecheck:
    {
      dir: ssorecheckDir,
      data: ssorecheckData,
      exe: ssorecheckExe
    }
  };

  return Config;
}

async function getProxies()
{
  const child = spawn(config.ssorecheck.exe, ['fetch_proxies'], 
  {
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });

  child.stdout.on('data', (data) =>
  {
    const raw = data.toString();
    console.log(raw);

    try
    {
      const msg = JSON.parse(raw.trim());

      if (msg.status === 'error') {
        client.message(msg.status, msg.message);
      }

      if (msg.status === 'fetchFinish') {
        client.message('success', msg.message);
        state.lastProxyFetch = Math.floor(Date.now() / 1000);
        saveState();
      }

      if (msg.status === 'scrapeFinish') {
        client.message('success', msg.message);
      }
    }
    catch {}
  });
}

app.on('before-quit', () =>
{
  if (gameWindow && !gameWindow.killed) {
    gameWindow.kill();
    tokenWatcher.stop();
  }
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
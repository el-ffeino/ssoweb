const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  nodeVersion: () => process.versions.node,
  getData: () => ipcRenderer.invoke('load-data'),
  minimizeApp: () => ipcRenderer.send('minimize-app'),
  closeApp: () => ipcRenderer.send('close-app'),
  saveData: (content) => ipcRenderer.invoke('save-data', content),
  Play: (account) => ipcRenderer.invoke('play', account),
  refreshAccount: (account) => ipcRenderer.invoke('refreshAccount', account),
  onSessionUpdated: (callback) => ipcRenderer.on('session-updated', (_, data) => callback(data)),
  onMessageReceived: (callback) => ipcRenderer.on('message', (_, data) => callback(data)),
  onAccountRefreshed: (callback) => ipcRenderer.on('account-refreshed', (_, data) => callback(data)),
  onInvalidCredentials: (callback) => ipcRenderer.on('invalid-credentials', (_, data) => callback(data))
});
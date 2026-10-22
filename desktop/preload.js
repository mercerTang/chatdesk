'use strict'

/**
 * 渲染进程桥：只暴露两个能力，页面代码照常在浏览器里跑（没有 window.desktop 就退回 Element Plus 通知）。
 * contextIsolation + sandbox 下，页面拿不到 Node，只能走这里列出的通道。
 */

const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('desktop', {
  isDesktop: true,
  platform: process.platform,
  // 当前后端地址（主进程 config.json 里的值，缺省 http://localhost:5180）
  getApiBase: () => ipcRenderer.invoke('desktop:get-api-base'),
  // 系统通知：{ title, body }，点击通知由主进程聚焦窗口
  notify: (payload) => ipcRenderer.invoke('desktop:notify', payload)
})

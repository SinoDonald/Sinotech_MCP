import fs from "fs";
import { RevitClientConnection } from "./SocketClient.js";

/**
 * Revit 連線端點：由 SINOTECH_MCP_CONFIG 指向的 JSON 檔（{ port, token, revitVersion }）提供，每次連線時重新讀取。
 * - AI助手：Revit 啟動背景程式時指定本次工作階段的檔案（動態埠號、每次啟動新權杖）
 * - 外部 Codex／Claude：「外部設定」寫入 %LOCALAPPDATA%\Sinotech\McpExternal\<年份>\external.json（20800＋年份）
 * 開發用途也可直接設定 SINOTECH_REVIT_PORT、SINOTECH_REVIT_TOKEN。
 */
function revitEndpoint(): { port: number; token: string; revitVersion?: string } {
  const file = process.env.SINOTECH_MCP_CONFIG;
  if (file) {
    let config: any;
    try {
      config = JSON.parse(fs.readFileSync(file, "utf8").replace(/^﻿/, ""));
    } catch {
      throw new Error(`讀不到 Revit 連線設定 ${file}：請在 Revit 開啟「外部AI服務」（或在「外部設定」重新套用）後再試。`);
    }
    const port = Number(config?.port);
    if (!Number.isInteger(port) || port < 1 || port > 65535 || typeof config?.token !== "string" || !config.token)
      throw new Error(`Revit 連線設定 ${file} 內容無效，請在 Revit「外部設定」重新套用。`);
    return { port, token: config.token, revitVersion: config.revitVersion };
  }
  const port = Number(process.env.SINOTECH_REVIT_PORT);
  if (Number.isInteger(port) && port > 0) return { port, token: process.env.SINOTECH_REVIT_TOKEN ?? "" };
  throw new Error("未設定 Revit 連線（SINOTECH_MCP_CONFIG）。請在 Revit 點選「外部設定」為此 AI 程式套用設定，並重新啟動 AI 程式。");
}

// Mutex to serialize all Revit connections - prevents race conditions
// when multiple requests are made in parallel
let connectionMutex: Promise<void> = Promise.resolve();

/**
 * 连接到Revit客户端并执行操作
 * @param operation 连接成功后要执行的操作函数
 * @returns 操作的结果
 */
export async function withRevitConnection<T>(
  operation: (client: RevitClientConnection) => Promise<T>
): Promise<T> {
  // Wait for any pending connection to complete before starting a new one
  const previousMutex = connectionMutex;
  let releaseMutex: () => void;
  connectionMutex = new Promise<void>((resolve) => {
    releaseMutex = resolve;
  });
  await previousMutex;

  let endpoint: ReturnType<typeof revitEndpoint>;
  try {
    endpoint = revitEndpoint();
  } catch (error) {
    releaseMutex!();
    throw error;
  }
  // Revit 端只監聽本機 IPv4，避免 localhost 解析成 ::1 而連不到
  const revitClient = new RevitClientConnection("127.0.0.1", endpoint.port, endpoint.token);
  const where = `Revit${endpoint.revitVersion ? " " + endpoint.revitVersion : ""}（127.0.0.1:${endpoint.port}）`;

  try {
    // 连接到Revit客户端
    if (!revitClient.isConnected) {
      await new Promise<void>((resolve, reject) => {
        const onConnect = () => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          resolve();
        };

        const onError = (error: any) => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          reject(new Error(`無法連線到 ${where}：請確認 Revit 已開啟，且「外部AI服務」為「開」（AI助手則需開啟面板）。`));
        };

        revitClient.socket.on("connect", onConnect);
        revitClient.socket.on("error", onError);

        revitClient.connect();

        setTimeout(() => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          reject(new Error(`連線到 ${where} 逾時。`));
        }, 5000);
      });
    }

    // 执行操作
    return await operation(revitClient);
  } finally {
    // 断开连接
    revitClient.disconnect();
    // Release the mutex so the next request can proceed
    releaseMutex!();
  }
}

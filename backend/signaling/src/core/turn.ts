import { createHmac } from 'node:crypto';

export interface TurnConfig {
  /** Shared secret with coturn (`static-auth-secret`). */
  secret: string;
  /** e.g. ["turn:turn.example.com:3478?transport=udp", "turns:turn.example.com:5349?transport=tcp"] */
  urls: string[];
  stunUrls: string[];
  ttlSeconds: number;
}

export interface IceServer {
  urls: string[];
  username?: string;
  credential?: string;
}

/** coturn REST API credentials (draft-uberti-behave-turn-rest). */
export function mintTurnCredentials(cfg: TurnConfig, deviceId: string, nowMs: number): { iceServers: IceServer[]; ttl: number } {
  const iceServers: IceServer[] = [];
  if (cfg.stunUrls.length) iceServers.push({ urls: cfg.stunUrls });
  if (cfg.secret && cfg.urls.length) {
    const username = `${Math.floor(nowMs / 1000) + cfg.ttlSeconds}:${deviceId}`;
    const credential = createHmac('sha1', cfg.secret).update(username).digest('base64');
    iceServers.push({ urls: cfg.urls, username, credential });
  }
  return { iceServers, ttl: cfg.ttlSeconds };
}

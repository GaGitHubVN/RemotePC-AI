import { jsonLogger, startNodeServer } from './server.ts';

const env = process.env;
const log = jsonLogger();
const list = (v: string | undefined) => (v ? v.split(',').map((s) => s.trim()).filter(Boolean) : []);

if (env.NODE_ENV === 'production' && env.TURN_URLS && (!env.TURN_SECRET || env.TURN_SECRET.length < 32)) {
  log.warn('config_error', { msg: 'TURN_SECRET must be set (>= 32 chars) when TURN_URLS is configured' });
  process.exit(1);
}

const turnUrls = list(env.TURN_URLS);
const stunUrls = list(env.STUN_URLS ?? 'stun:stun.l.google.com:19302');

const srv = await startNodeServer({
  port: Number(env.PORT ?? 8787),
  host: env.HOST ?? '0.0.0.0',
  trustProxy: env.TRUST_PROXY === '1',
  log,
  turn: { secret: env.TURN_SECRET ?? '', urls: turnUrls, stunUrls, ttlSeconds: Number(env.TURN_TTL ?? 3600) },
});
log.info('listening', { port: srv.port, turn: turnUrls.length > 0 });

const shutdown = () => void srv.close().then(() => process.exit(0));
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

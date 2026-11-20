import { z } from 'zod';

const b64u = (min: number, max: number) =>
  z.string().regex(/^[A-Za-z0-9_-]*$/).min(min).max(max);

export const DeviceIdSchema = z.string().regex(/^[a-z2-7]{26}$/);

export const EnvelopeSchema = z
  .object({
    v: z.literal(1),
    typ: z.string().regex(/^[a-z][a-z0-9._-]{0,40}$/),
    from: DeviceIdSchema,
    to: DeviceIdSchema,
    sid: z.string().max(32).regex(/^[A-Za-z0-9_-]*$/),
    seq: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER),
    ts: z.number().int().min(0),
    n: b64u(16, 16),
    kx: z.object({ edPk: b64u(43, 43), xPk: b64u(43, 43) }).strict().optional(),
    ct: b64u(22, 65_536),
  })
  .strict();

export const HelloSchema = z
  .object({
    t: z.literal('hello'),
    v: z.literal(1),
    role: z.enum(['host', 'client']),
    id: DeviceIdSchema,
    edPk: b64u(43, 43),
    sig: b64u(86, 86),
  })
  .strict();

export const ClientMessageSchema = z.discriminatedUnion('t', [
  z.object({ t: z.literal('relay'), to: DeviceIdSchema, env: EnvelopeSchema }).strict(),
  z.object({ t: z.literal('presence'), ids: z.array(DeviceIdSchema).max(32) }).strict(),
  z.object({ t: z.literal('turn') }).strict(),
  z.object({ t: z.literal('ping') }).strict(),
]);

export type ClientMessage = z.infer<typeof ClientMessageSchema>;

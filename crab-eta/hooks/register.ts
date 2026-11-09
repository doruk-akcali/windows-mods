import type { EngineInterface, Register } from 'claude-code'

// The popup (popup/crab-popup.ps1) watches this file and slides the crab in
// while `active` is true. It lives outside the mod folder so writing it never
// reloads the mod.
type CrabState = {
  active: boolean
  minutes: number
  task: string
  setAt: number
  // The 5-hour session usage in percent (-1 before the first reading); the
  // popup colors the crab by it.
  usage: number
  // The 7-day (weekly) usage in percent, -1 likewise; it colors the crab's flag.
  weekly: number
}

type TaskState = Omit<CrabState, 'usage' | 'weekly'>
type RateLimits = readonly { kind: string; percentUsed: number }[]

const TOOL = 'eta'
const LONG_TASK_MINUTES = 3

const GUIDE = [
  `When you start a task you expect to take more than ${LONG_TASK_MINUTES} minutes,`,
  `call the mcp__crab-eta__${TOOL} tool with your honest estimate of the minutes`,
  'remaining and a short label for the task. Estimate tightly from the steps left;',
  'do not pad. Call it again at each milestone (e.g. after a build, after tests,',
  'before the final write-up) with the minutes now left, earlier or later than',
  'planned. Do not call it for short tasks or plain answers.',
].join(' ')

let stateFile: string | undefined
let isActive = false
let usage = -1
let weekly = -1
let last: TaskState = { active: false, minutes: 0, task: '', setAt: 0 }

async function writeState($: EngineInterface, state: TaskState) {
  last = state
  if (stateFile === undefined) return
  const full: CrabState = { ...state, usage, weekly }
  await $.fs.write(stateFile, JSON.stringify(full))
}

function percentOf(limits: RateLimits, kind: string, fallback: number) {
  return limits.find(limit => limit.kind === kind)?.percentUsed ?? fallback
}

async function setUsage($: EngineInterface, limits: RateLimits) {
  const session = percentOf(limits, 'five_hour', usage)
  const week = percentOf(limits, 'seven_day', weekly)
  if (session === usage && week === weekly) return
  usage = session
  weekly = week
  await writeState($, last)
}

async function show($: EngineInterface, minutes: number, task: string) {
  isActive = true
  await writeState($, { active: true, minutes, task, setAt: await $.clock.now() })
  $.ui.status(`🦀 ~${Math.max(1, Math.ceil(minutes))} min left`)
}

async function finish($: EngineInterface) {
  if (!isActive) return
  isActive = false
  await writeState($, { active: false, minutes: 0, task: '', setAt: await $.clock.now() })
  $.ui.status(undefined)
}

async function runPopup($: EngineInterface, file: string) {
  const child = $.process.spawn({
    argv: [
      'powershell.exe', '-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
      '-File', `${$.plugin.root}\\popup\\crab-popup.ps1`, '-StateFile', file,
    ],
  })
  for await (const { text } of child) $.ui.log(text, { to: 'debug' })
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const started = await next(e)
    const base = await $.env.get('LOCALAPPDATA')
    if (base === undefined) return started

    stateFile = `${base}\\crab-eta\\state.json`
    const { rateLimits } = await $.session.usage()
    usage = percentOf(rateLimits, 'five_hour', -1)
    weekly = percentOf(rateLimits, 'seven_day', -1)
    await writeState($, { active: false, minutes: 0, task: '', setAt: 0 })

    await $.tool.register({
      name: TOOL,
      description:
        'Shows the user a crab at the bottom-right of their screen with the minutes left on a long task. ' +
        GUIDE,
      inputSchema: {
        type: 'object',
        properties: {
          minutes: { type: 'number', description: 'Estimated minutes until the task is done.' },
          task: { type: 'string', description: 'A short label for the task (a few words).' },
        },
        required: ['minutes'],
      },
      isDeferred: false,
    })

    await $.command.register({
      name: 'crab-test',
      description: 'Shows the ETA crab for 20 seconds, to check the popup works.',
      argumentHint: '[minutes]',
    })

    // One popup process for the session; the script keeps a single instance
    // across sessions and ends when this module unloads.
    void runPopup($, stateFile)

    return started
  })

  on('prompt.compose', async ($, e, next) => {
    const composed = await next(e)
    return {
      sections: [...composed.sections, { id: 'crab-eta:guide', text: GUIDE, scope: 'session' }],
    }
  })

  on('tool.call', { tool: 'mcp__crab-eta__eta' }, async ($, e) => {
    const input = e as unknown as { minutes?: unknown; task?: unknown }
    const minutes = Number(input.minutes)
    if (!Number.isFinite(minutes) || minutes < 0) {
      return { deny: 'minutes must be a number of minutes, 0 or more.' }
    }
    const task = typeof input.task === 'string' ? input.task.slice(0, 60) : ''

    if (minutes === 0) {
      await finish($)
      return { result: 'The crab says the task is done.' }
    }
    await show($, minutes, task)
    return { result: `The crab shows ~${Math.ceil(minutes)} min left.` }
  })

  on('command.run', { command: 'crab-test' }, async ($, e) => {
    const minutes = Number(e.args) > 0 ? Number(e.args) : 2
    await show($, minutes, 'Testing the crab')
    $.clock.after(20_000, () => void finish($))
    return { text: `The crab is up with ~${minutes} min for 20 seconds.` }
  })

  // The main loop's turn ended: the task is over, so the crab says done.
  on('turn.complete', async ($, e, next) => {
    if (e.agentId === undefined) await finish($)
    return next(e)
  })

  // A rate-limit window moved: recolor the crab.
  on('session.measure', async ($, e, next) => {
    if (e.changed.includes('rateLimits')) await setUsage($, e.rateLimits)
    return next(e)
  })

  on('session.end', async ($, e, next) => {
    await finish($)
    return next(e)
  })
}

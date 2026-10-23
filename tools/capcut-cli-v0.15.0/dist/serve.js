import { spawn } from "node:child_process";
import { closeSync, createReadStream, existsSync, mkdtempSync, openSync, readFileSync, rmSync, statSync, } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";
/**
 * Bounded JSONL job runner for n8n/Make/Coze/cron.
 *
 * Jobs for different projects may run in parallel; jobs targeting the same
 * project are serialized so two writers never race. An optional stable `id`
 * deduplicates retries from external orchestrators. Each job has bounded
 * output, configurable timeout, retry/backoff, and one JSON result line.
 */
export async function serveQueue(opts) {
    if (opts.queuePath && !existsSync(opts.queuePath))
        throw new Error(`Queue file not found: ${opts.queuePath}`);
    const input = opts.queuePath ? createReadStream(opts.queuePath, "utf-8") : process.stdin;
    const reader = createInterface({ input, crlfDelay: Infinity });
    const jobs = [];
    const immediate = [];
    for await (const rawLine of reader) {
        const line = rawLine.trim();
        if (!line)
            continue;
        try {
            const job = JSON.parse(line);
            if (!job || typeof job.cmd !== "string" || job.cmd.length === 0) {
                throw new Error("missing or invalid 'cmd' field");
            }
            jobs.push(job);
        }
        catch (error) {
            const result = {
                ok: false,
                cmd: "",
                args: [],
                status: null,
                stderr: `JSON parse error: ${error instanceof Error ? error.message : String(error)}`,
            };
            immediate.push(result);
            writeResult(result);
            if (opts.failFast)
                break;
        }
    }
    let succeeded = 0;
    let failed = immediate.length;
    let deduplicated = 0;
    const projectLocks = new Map();
    const idResults = new Map();
    const execute = async (job) => {
        if (job.id) {
            const previous = idResults.get(job.id);
            if (previous) {
                const result = await previous;
                return { ...result, deduplicated: true };
            }
        }
        const operation = withProjectLock(projectLocks, job.project, () => runWithRetries(opts, job));
        if (job.id)
            idResults.set(job.id, operation);
        return operation;
    };
    const record = async (job) => {
        const result = await execute(job);
        writeResult(result);
        if (result.deduplicated)
            deduplicated++;
        if (result.ok)
            succeeded++;
        else
            failed++;
        return result.ok;
    };
    if (opts.failFast) {
        for (const job of jobs) {
            if (!(await record(job)))
                break;
        }
    }
    else {
        const workerCount = Math.max(1, Math.min(Math.floor(opts.workers ?? 1), 32));
        let cursor = 0;
        const workers = Array.from({ length: workerCount }, async () => {
            while (cursor < jobs.length) {
                const index = cursor++;
                await record(jobs[index]);
            }
        });
        await Promise.all(workers);
    }
    return { succeeded, failed, deduplicated };
}
function withProjectLock(locks, project, operation) {
    if (!project)
        return operation();
    const previous = locks.get(project) ?? Promise.resolve();
    const current = previous.catch(() => undefined).then(operation);
    locks.set(project, current.then(() => undefined, () => undefined));
    return current;
}
async function runWithRetries(opts, job) {
    const started = Date.now();
    const retries = Math.max(0, Math.floor(job.retries ?? opts.retries ?? 0));
    let result = { ok: false, cmd: job.cmd, args: [], status: null };
    for (let attempt = 1; attempt <= retries + 1; attempt++) {
        result = await runJob(opts.cliPath, job, {
            timeoutMs: job.timeoutMs ?? opts.timeoutMs ?? 300_000,
            maxBufferBytes: opts.maxBufferBytes ?? 16 * 1024 * 1024,
        });
        result.attempts = attempt;
        if (result.ok)
            break;
        if (attempt <= retries)
            await delay((opts.backoffMs ?? 250) * 2 ** (attempt - 1));
    }
    result.duration_ms = Date.now() - started;
    return result;
}
function runJob(cliPath, job, limits) {
    const args = [job.cmd];
    if (job.project)
        args.push(job.project);
    if (Array.isArray(job.args))
        args.push(...job.args.map(String));
    return new Promise((resolve) => {
        const captureDir = mkdtempSync(join(tmpdir(), "capcut-serve-"));
        const stdoutPath = join(captureDir, "stdout");
        const stderrPath = join(captureDir, "stderr");
        const stdoutFd = openSync(stdoutPath, "w");
        const stderrFd = openSync(stderrPath, "w");
        const child = spawn(process.execPath, [cliPath, ...args], { stdio: ["ignore", stdoutFd, stderrFd] });
        closeSync(stdoutFd);
        closeSync(stderrFd);
        let settled = false;
        let timedOut = false;
        let overflow = false;
        const finish = (result) => {
            if (settled)
                return;
            settled = true;
            clearTimeout(timer);
            clearInterval(bufferWatch);
            rmSync(captureDir, { recursive: true, force: true });
            resolve(result);
        };
        const timer = setTimeout(() => {
            timedOut = true;
            child.kill("SIGKILL");
        }, limits.timeoutMs);
        const bufferWatch = setInterval(() => {
            const bytes = [stdoutPath, stderrPath].reduce((sum, path) => {
                try {
                    return sum + statSync(path).size;
                }
                catch {
                    return sum;
                }
            }, 0);
            if (bytes > limits.maxBufferBytes) {
                overflow = true;
                child.kill("SIGKILL");
            }
        }, 25);
        child.on("error", (error) => finish({ id: job.id, ok: false, cmd: job.cmd, args, status: null, stderr: error.message }));
        child.on("close", (status) => {
            if (settled)
                return;
            const out = readFileSync(stdoutPath, "utf-8");
            const err = readFileSync(stderrPath, "utf-8").trim();
            let parsed = out;
            try {
                if (out.trim())
                    parsed = JSON.parse(out);
            }
            catch {
                // Keep text output unchanged.
            }
            finish({
                id: job.id,
                ok: status === 0 && !timedOut && !overflow,
                cmd: job.cmd,
                args,
                status,
                stdout: parsed,
                stderr: overflow
                    ? `output exceeded ${limits.maxBufferBytes} bytes`
                    : timedOut
                        ? `timed out after ${limits.timeoutMs}ms`
                        : err || undefined,
                timed_out: timedOut || undefined,
                overflow: overflow || undefined,
            });
        });
    });
}
function delay(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
}
function writeResult(result) {
    process.stdout.write(`${JSON.stringify(result)}\n`);
}

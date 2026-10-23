import { spawnSync } from "node:child_process";
import { closeSync, mkdtempSync, openSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { commandNames } from "./command-specs.js";
/** Typed, side-effect-free-to-import runner generated from the command registry. */
export function runCommand(request) {
    if (!commandNames().includes(request.command)) {
        throw new Error(`Unknown capcut command: ${request.command}`);
    }
    const cliPath = fileURLToPath(new URL("./index.js", import.meta.url));
    const args = [request.command];
    if (request.project)
        args.push(request.project);
    if (request.args)
        args.push(...request.args.map(String));
    const captureDir = mkdtempSync(join(tmpdir(), "capcut-runner-"));
    const stdoutPath = join(captureDir, "stdout");
    const stderrPath = join(captureDir, "stderr");
    const stdoutFd = openSync(stdoutPath, "w");
    const stderrFd = openSync(stderrPath, "w");
    let result;
    try {
        result = spawnSync(process.execPath, [cliPath, ...args], {
            cwd: request.cwd,
            input: request.input,
            timeout: request.timeoutMs ?? 300_000,
            stdio: ["pipe", stdoutFd, stderrFd],
        });
    }
    finally {
        closeSync(stdoutFd);
        closeSync(stderrFd);
    }
    const stdout = readFileSync(stdoutPath, "utf-8");
    const stderr = readFileSync(stderrPath, "utf-8");
    rmSync(captureDir, { recursive: true, force: true });
    let json = null;
    try {
        if (stdout.trim())
            json = JSON.parse(stdout);
    }
    catch {
        // Commands such as export-srt intentionally return text.
    }
    return {
        ok: result.status === 0,
        status: result.status,
        stdout,
        stderr: stderr || result.error?.message || "",
        json,
    };
}

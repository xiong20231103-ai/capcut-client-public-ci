import { type CommandName } from "./command-specs.js";
export interface RunCommandRequest<T extends CommandName = CommandName> {
    command: T;
    project?: string;
    args?: Array<string | number | boolean>;
    input?: string;
    cwd?: string;
    timeoutMs?: number;
}
export interface RunCommandResult<T = unknown> {
    ok: boolean;
    status: number | null;
    stdout: string;
    stderr: string;
    json: T | null;
}
/** Typed, side-effect-free-to-import runner generated from the command registry. */
export declare function runCommand<T = unknown>(request: RunCommandRequest): RunCommandResult<T>;

export type CheckStatus = "ok" | "warn" | "missing";
export interface DoctorCheck {
    name: string;
    status: CheckStatus;
    detail: string;
    /** Commands degraded or unavailable when this check is not "ok". */
    affects?: string[];
    /** How to fix, when not "ok". */
    fix?: string;
}
export interface DoctorReport {
    ok: boolean;
    platform: string;
    node: string;
    checks: DoctorCheck[];
}
/** Default per-OS CapCut/JianYing project directories. */
export declare function draftDirs(): {
    label: string;
    path: string;
}[];
export declare function runDoctor(): DoctorReport;

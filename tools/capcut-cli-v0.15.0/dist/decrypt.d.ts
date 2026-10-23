export interface EncryptionReport {
    encrypted: boolean;
    filePath: string;
    size: number;
    reason: string;
    fix: string;
}
/**
 * Detect whether a draft_content.json is encrypted (JianYing 6.0+).
 *
 * JianYing introduced AES-based encryption in 6.0+. The encrypted payload is
 * not valid JSON — it's binary. This function reports the situation clearly
 * and points to the documented workarounds rather than attempting to decrypt:
 * shipping a decryption routine has legal posture implications and the
 * community-known algorithms are in flux. See:
 *   - https://github.com/GuanYixuan/pyJianYingDraft/issues/142
 *   - https://github.com/GuanYixuan/pyJianYingDraft/issues/169
 *   - https://github.com/GuanYixuan/pyJianYingDraft/issues/174
 *   - https://github.com/duoec/duo-video (reference implementation)
 */
export declare function detectEncryption(filePath: string): EncryptionReport;

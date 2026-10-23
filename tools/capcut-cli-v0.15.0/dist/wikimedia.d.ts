export type LicenseClass = "permissive" | "fair-use" | "restrictive" | "unknown";
export interface WikimediaAsset {
    fileTitle: string;
    directUrl: string;
    descriptionUrl: string;
    mime: string;
    sizeBytes: number | null;
    width: number | null;
    height: number | null;
    license: {
        raw: string | null;
        class: LicenseClass;
        artist: string | null;
        credit: string | null;
    };
}
/** Is this a supported Wikimedia-family URL? Use as a gate before fetching. */
export declare function isWikimediaUrl(u: string): boolean;
/** Classify a LicenseShortName string from extmetadata.LicenseShortName.value. */
export declare function classifyLicense(raw: string | null | undefined): LicenseClass;
/** Extract "File:Foo.jpg" from any accepted Wikimedia URL. null if URL is an API call that needs different handling. */
export declare function extractFileTitle(u: string): string | null;
/** Call Commons imageinfo for a File: title and build a license-aware asset record. */
export declare function resolveWikimediaAsset(inputUrl: string): Promise<WikimediaAsset>;
/** Stream-download the asset's directUrl to destPath. Creates parent dir. */
export declare function downloadAsset(asset: WikimediaAsset, destPath: string): Promise<void>;
export interface FetchOptions {
    forceLicense?: boolean;
    destDir: string;
    destFilename?: string;
}
export interface FetchResult {
    localPath: string;
    asset: WikimediaAsset;
    warning?: string;
}
/** High-level entry: URL in, local file + metadata out, license-gated. */
export declare function fetchWikimediaAsset(inputUrl: string, opts: FetchOptions): Promise<FetchResult>;

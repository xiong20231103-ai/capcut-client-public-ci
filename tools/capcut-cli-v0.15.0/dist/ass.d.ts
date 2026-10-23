export interface AssCue {
    index: number;
    startUs: number;
    endUs: number;
    text: string;
    style?: string;
}
export declare function parseAss(content: string): AssCue[];

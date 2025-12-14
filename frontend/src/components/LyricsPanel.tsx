import React, { useEffect, useState, useRef } from 'react';
import { getLyrics, generateLyrics, getAiStatus, type Lyric } from '../services/api';

interface LyricsPanelProps {
    mediaId: number;
    currentTime: number; // Current playback time in seconds
    onClose: () => void;
}

interface LrcLine {
    time: number; // Seconds
    text: string;
}

const parseLrc = (lrc: string): LrcLine[] => {
    const lines = lrc.split('\n');
    const result: LrcLine[] = [];
    const regex = /\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)/;

    for (const line of lines) {
        const match = line.match(regex);
        if (match) {
            const min = parseInt(match[1]);
            const sec = parseInt(match[2]);
            const ms = parseInt(match[3]);
            // ms can be 2 or 3 digits. 
            const time = min * 60 + sec + (ms / (match[3].length === 3 ? 1000 : 100));
            result.push({ time, text: match[4].trim() });
        }
    }
    return result;
};

export const LyricsPanel: React.FC<LyricsPanelProps> = ({ mediaId, currentTime, onClose }) => {
    const [lyricData, setLyricData] = useState<Lyric | null>(null);
    const [parsedLines, setParsedLines] = useState<LrcLine[]>([]);
    const [loading, setLoading] = useState(false);
    const [generating, setGenerating] = useState(false);
    const [aiAvailable, setAiAvailable] = useState(false);
    const [error, setError] = useState<string | null>(null);

    // AI Options
    const [lang, setLang] = useState('');
    const [customPrompt, setCustomPrompt] = useState('');

    const scrollContainerRef = useRef<HTMLDivElement>(null);
    const activeLineRef = useRef<HTMLDivElement>(null);

    // Initial Load
    useEffect(() => {
        loadLyrics();
        checkAi();
    }, [mediaId]);

    // Parse LRC when content changes
    useEffect(() => {
        if (lyricData?.content) {
            setParsedLines(parseLrc(lyricData.content));
        }
    }, [lyricData]);

    // ... (Scroll effect skipped) ...
    // Auto-scroll to active line
    useEffect(() => {
        if (activeLineRef.current && scrollContainerRef.current) {
            activeLineRef.current.scrollIntoView({
                behavior: 'smooth',
                block: 'center',
            });
        }
    }, [currentTime]);

    const loadLyrics = async () => {
        // ... (Skipped)
        setLoading(true);
        setError(null);
        try {
            const data = await getLyrics(mediaId);
            setLyricData(data);
        } catch (err: any) {
            setLyricData(null);
        } finally {
            setLoading(false);
        }
    };

    const checkAi = async () => {
        // ... (Skipped)
        try {
            const status = await getAiStatus();
            setAiAvailable(status.available);
        } catch {
            setAiAvailable(false);
        }
    };

    const handleGenerate = async () => {
        setGenerating(true);
        setError(null);
        try {
            // Pass lang and prompt
            const data = await generateLyrics(mediaId, lang, customPrompt);
            setLyricData(data);
        } catch (err: any) {
            setError("Failed to generate lyrics. Is the server busy?");
        } finally {
            setGenerating(false);
        }
    };

    // Find active line
    // findLastIndex polyfill:
    let activeIndex = -1;
    for (let i = parsedLines.length - 1; i >= 0; i--) {
        if (parsedLines[i].time <= currentTime) {
            activeIndex = i;
            break;
        }
    }

    return (
        <div className="fixed inset-y-0 right-0 w-80 md:w-96 bg-gray-900/95 backdrop-blur-xl border-l border-white/10 shadow-2xl z-50 flex flex-col transition-all duration-300">
            {/* Header */}
            <div className="p-4 border-b border-white/10 flex justify-between items-center">
                <h2 className="text-lg font-bold text-white tracking-wide">Lyrics</h2>
                <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition text-gray-400 hover:text-white">
                    ✕
                </button>
            </div>

            {/* Content */}
            <div className="flex-1 overflow-y-auto p-4 relative" ref={scrollContainerRef}>
                {loading ? (
                    <div className="flex justify-center items-center h-full text-gray-400 animate-pulse">
                        Checking for lyrics...
                    </div>
                ) : lyricData ? (
                    <div className="space-y-6 py-10">
                        {parsedLines.length > 0 ? parsedLines.map((line, idx) => (
                            <div
                                key={idx}
                                ref={idx === activeIndex ? activeLineRef : null}
                                className={`text-center transition-all duration-500 cursor-default
                                    ${idx === activeIndex
                                        ? 'text-white text-xl font-bold scale-105 blur-0'
                                        : 'text-gray-500 text-base blur-[0.5px] hover:text-gray-300 hover:blur-0'}`}
                            >
                                {line.text}
                            </div>
                        )) : (
                            <div className="text-center text-gray-400">Not valid LRC format</div>
                        )}
                        <div className="pt-8 text-xs text-center text-gray-600">
                            Source: {lyricData.source}
                        </div>
                    </div>
                ) : (
                    // No Lyrics State
                    <div className="flex flex-col items-center justify-center h-full space-y-4">
                        <div className="text-6xl text-gray-700">♪</div>
                        <p className="text-gray-400">No lyrics found.</p>

                        {generating ? (
                            <div className="flex flex-col items-center space-y-2">
                                <div className="w-6 h-6 border-2 border-purple-500 border-t-transparent rounded-full animate-spin"></div>
                                <span className="text-sm text-purple-400 animate-pulse">AI is listening... (this takes ~30s)</span>
                            </div>
                        ) : aiAvailable ? (
                            <div className="flex flex-col items-center gap-3 w-full px-8">
                                <div className="grid grid-cols-2 gap-2 w-full">
                                    <select
                                        className="bg-gray-800 border border-gray-700 text-white text-xs rounded p-2 focus:ring-2 focus:ring-purple-500 outline-none"
                                        value={lang}
                                        onChange={(e) => setLang(e.target.value)}
                                    >
                                        <option value="">Auto Language</option>
                                        <option value="zh">Chinese (zh)</option>
                                        <option value="en">English (en)</option>
                                        <option value="ja">Japanese (ja)</option>
                                        <option value="ko">Korean (ko)</option>
                                    </select>
                                    <input
                                        type="text"
                                        className="bg-gray-800 border border-gray-700 text-white text-xs rounded p-2 focus:ring-2 focus:ring-purple-500 outline-none"
                                        placeholder="Prompt (e.g. 繁體中文)"
                                        value={customPrompt}
                                        onChange={(e) => setCustomPrompt(e.target.value)}
                                    />
                                </div>

                                <button
                                    onClick={handleGenerate}
                                    className="w-full px-6 py-2 bg-gradient-to-r from-purple-600 to-indigo-600 hover:from-purple-500 hover:to-indigo-500 text-white rounded-lg transition shadow-lg shadow-purple-900/30 flex items-center justify-center gap-2 font-medium"
                                >
                                    ✨ AI Generate
                                </button>
                                <p className="text-[10px] text-gray-500">
                                    Use 'zh' + Prompt '繁體中文' for Traditional Chinese.
                                </p>
                            </div>
                        ) : (
                            <div className="text-xs text-red-400 bg-red-900/20 px-3 py-1 rounded">
                                AI Service Offline
                            </div>
                        )}

                        {error && <div className="text-red-400 text-sm px-4 text-center">{error}</div>}
                    </div>
                )}
            </div>
        </div>
    );
};

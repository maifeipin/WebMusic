import React, { useEffect, useState, useRef } from 'react';
import { getLyrics, generateLyrics, getAiStatus, optimizeLyrics, type Lyric } from '../services/api';
import { Sparkles, Copy, Check, Edit3, X, Mic2, Music, RefreshCw, Volume2, ArrowDownCircle } from 'lucide-react';

interface LyricsPanelProps {
    mediaId: number;
    currentTime: number; // Current playback time in seconds
    onClose: () => void;
    song?: {
        title?: string;
        artist?: string;
        album?: string;
        coverArt?: string;
    };
    onSeek?: (time: number) => void;
}

interface LrcLine {
    time: number; // Seconds
    text: string;
}

const parseLrc = (lrc: string): LrcLine[] => {
    if (!lrc) return [];
    const lines = lrc.split('\n');
    const result: LrcLine[] = [];
    const regex = /\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)/;

    for (const line of lines) {
        const match = line.match(regex);
        if (match) {
            const min = parseInt(match[1], 10);
            const sec = parseInt(match[2], 10);
            const ms = parseInt(match[3], 10);
            const time = min * 60 + sec + (ms / (match[3].length === 3 ? 1000 : 100));
            const text = match[4].trim();
            if (text) {
                result.push({ time, text });
            }
        }
    }
    return result.sort((a, b) => a.time - b.time);
};

export const LyricsPanel: React.FC<LyricsPanelProps> = ({ mediaId, currentTime, onClose, song, onSeek }) => {
    const [lyricData, setLyricData] = useState<Lyric | null>(null);
    const [parsedLines, setParsedLines] = useState<LrcLine[]>([]);
    const [loading, setLoading] = useState(false);
    const [generating, setGenerating] = useState(false);
    const [elapsedSeconds, setElapsedSeconds] = useState(0);
    const [polishing, setPolishing] = useState(false);
    const [aiAvailable, setAiAvailable] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [copied, setCopied] = useState(false);

    // Manual Edit Mode
    const [isEditing, setIsEditing] = useState(false);
    const [editText, setEditText] = useState('');
    const [savingEdit, setSavingEdit] = useState(false);

    // Font Scaling
    const [fontSizeLevel, setFontSizeLevel] = useState<number>(0); // -1: small, 0: medium, 1: large

    // User manual scroll detection
    const [userScrolled, setUserScrolled] = useState(false);
    const scrollTimeoutRef = useRef<any | null>(null);

    // AI Options
    const [lang, setLang] = useState('');
    const [customPrompt, setCustomPrompt] = useState('');

    const scrollContainerRef = useRef<HTMLDivElement>(null);
    const activeLineRef = useRef<HTMLDivElement>(null);
    const isMounted = useRef(true);

    useEffect(() => {
        isMounted.current = true;
        return () => {
            isMounted.current = false;
        };
    }, []);

    // Initial Load & AI Health Check
    useEffect(() => {
        loadLyrics();
        checkAi();
        setIsEditing(false);
    }, [mediaId]);

    // Parse LRC when content changes
    useEffect(() => {
        if (lyricData?.content) {
            setParsedLines(parseLrc(lyricData.content));
            setEditText(lyricData.content);
        } else {
            setParsedLines([]);
            setEditText('');
        }
    }, [lyricData]);

    // Timer while generating
    useEffect(() => {
        let timer: any | null = null;
        if (generating) {
            setElapsedSeconds(0);
            timer = setInterval(() => {
                setElapsedSeconds(prev => prev + 1);
            }, 1000);
        } else {
            setElapsedSeconds(0);
        }
        return () => {
            if (timer) clearInterval(timer);
        };
    }, [generating]);

    // Auto-scroll to active line if user is not actively scrolling
    useEffect(() => {
        if (!userScrolled && activeLineRef.current && scrollContainerRef.current && !isEditing) {
            activeLineRef.current.scrollIntoView({
                behavior: 'smooth',
                block: 'center',
            });
        }
    }, [currentTime, userScrolled, isEditing]);

    const handleContainerScroll = () => {
        setUserScrolled(true);
        if (scrollTimeoutRef.current) clearTimeout(scrollTimeoutRef.current);
        scrollTimeoutRef.current = setTimeout(() => {
            if (isMounted.current) {
                setUserScrolled(false);
            }
        }, 4000);
    };

    const scrollToCurrentLine = () => {
        setUserScrolled(false);
        if (activeLineRef.current) {
            activeLineRef.current.scrollIntoView({
                behavior: 'smooth',
                block: 'center',
            });
        }
    };

    const loadLyrics = async () => {
        setLoading(true);
        setError(null);
        try {
            const data = await getLyrics(mediaId);
            if (isMounted.current) {
                setLyricData(data);
            }
        } catch {
            if (isMounted.current) {
                setLyricData(null);
            }
        } finally {
            if (isMounted.current) {
                setLoading(false);
            }
        }
    };

    const checkAi = async () => {
        try {
            const status = await getAiStatus();
            if (isMounted.current) setAiAvailable(status.available);
        } catch {
            if (isMounted.current) setAiAvailable(false);
        }
    };

    // AI Generation with intelligent polling fallback
    const handleGenerate = async () => {
        setGenerating(true);
        setError(null);

        // Start background polling in case proxy or HTTP request drops/times out
        let pollTimer: any | null = null;
        let isDone = false;

        const stopPolling = () => {
            if (pollTimer) {
                clearInterval(pollTimer);
                pollTimer = null;
            }
        };

        pollTimer = setInterval(async () => {
            if (!isMounted.current || isDone) return;
            try {
                const checked = await getLyrics(mediaId);
                if (checked && checked.content) {
                    isDone = true;
                    stopPolling();
                    if (isMounted.current) {
                        setLyricData(checked);
                        setGenerating(false);
                    }
                }
            } catch {
                // Ignore 404 while generating
            }
        }, 3000);

        try {
            const data = await generateLyrics(mediaId, lang, customPrompt);
            isDone = true;
            stopPolling();
            if (isMounted.current) {
                setLyricData(data);
            }
        } catch (err: any) {
            if (!isDone) {
                // If direct post timed out or errored, check one more time with a short delay
                setTimeout(async () => {
                    if (!isMounted.current) return;
                    try {
                        const finalCheck = await getLyrics(mediaId);
                        if (finalCheck && finalCheck.content) {
                            setLyricData(finalCheck);
                            stopPolling();
                            setGenerating(false);
                            return;
                        }
                    } catch { }
                    if (isMounted.current && !isDone) {
                        setError("AI 生成请求超时或失败，请检查服务状态或重试。");
                        setGenerating(false);
                        stopPolling();
                    }
                }, 2000);
            }
        } finally {
            if (isDone && isMounted.current) {
                setGenerating(false);
                stopPolling();
            }
        }
    };

    const handlePolish = async () => {
        if (!lyricData?.content) return;
        setPolishing(true);
        try {
            const newContent = await optimizeLyrics(lyricData.content, mediaId);
            if (isMounted.current) {
                setLyricData({ ...lyricData, content: newContent, source: 'Gemini (Polished)' });
            }
        } catch {
            if (isMounted.current) setError("Gemini 润色失败，请重试。");
        } finally {
            if (isMounted.current) setPolishing(false);
        }
    };

    const handleCopy = async () => {
        if (!lyricData?.content) return;
        try {
            await navigator.clipboard.writeText(lyricData.content);
            setCopied(true);
            setTimeout(() => {
                if (isMounted.current) setCopied(false);
            }, 2000);
        } catch { }
    };

    const handleSaveManualEdit = async () => {
        if (!editText.trim()) return;
        setSavingEdit(true);
        try {
            // Save manual edit via optimize endpoint with raw content
            await optimizeLyrics(editText, mediaId);
            setLyricData({
                id: lyricData?.id || 0,
                content: editText,
                language: lyricData?.language || 'manual',
                source: 'User Edited',
                version: 'manual',
                Title: song?.title || lyricData?.Title,
                Artist: song?.artist || lyricData?.Artist
            });
            setIsEditing(false);
        } catch {
            setError("保存歌词失败，请重试。");
        } finally {
            setSavingEdit(false);
        }
    };

    // Find active line index
    let activeIndex = -1;
    for (let i = parsedLines.length - 1; i >= 0; i--) {
        if (parsedLines[i].time <= currentTime) {
            activeIndex = i;
            break;
        }
    }

    const titleText = song?.title || lyricData?.Title || '歌词面板';
    const artistText = song?.artist || lyricData?.Artist || 'Unknown Artist';

    return (
        <div className="fixed inset-0 z-[80] overflow-hidden">
            {/* Backdrop */}
            <div 
                className="absolute inset-0 bg-black/60 backdrop-blur-sm transition-opacity duration-300 animate-fade-in"
                onClick={onClose}
            />

            {/* Sliding Drawer Container */}
            <div className="absolute inset-y-0 right-0 max-w-full flex pl-6 sm:pl-10">
                <div className="w-screen max-w-full sm:max-w-md md:max-w-lg bg-gray-900/95 backdrop-blur-2xl border-l border-white/10 shadow-2xl flex flex-col transform transition-all duration-300 ease-out animate-slide-left">
                    
                    {/* Drawer Header */}
                    <div className="p-4 border-b border-white/10 bg-gray-900/80 backdrop-blur-md flex items-center justify-between gap-3">
                        <div className="flex items-center gap-3 overflow-hidden flex-1">
                            <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-purple-600/30 to-indigo-600/30 border border-purple-500/20 flex items-center justify-center flex-shrink-0 text-purple-400 shadow-inner">
                                <Music size={20} />
                            </div>
                            <div className="flex flex-col min-w-0">
                                <div className="flex items-center gap-2">
                                    <h2 className="text-sm font-bold text-white truncate max-w-[200px] sm:max-w-[240px]">
                                        {titleText}
                                    </h2>
                                    {lyricData?.source && (
                                        <span className="text-[10px] px-1.5 py-0.5 rounded-full bg-purple-500/20 text-purple-300 border border-purple-500/30 font-medium whitespace-nowrap">
                                            {lyricData.source.includes('Whisper') ? 'Whisper AI' : lyricData.source.includes('Gemini') ? 'Gemini 润色' : lyricData.source}
                                        </span>
                                    )}
                                </div>
                                <span className="text-xs text-gray-400 truncate">{artistText}</span>
                            </div>
                        </div>

                        {/* Top Actions */}
                        <div className="flex items-center gap-1.5 flex-shrink-0">
                            {lyricData && !isEditing && (
                                <>
                                    <button
                                        onClick={() => setFontSizeLevel(prev => (prev === 1 ? -1 : prev + 1))}
                                        title="调节歌词字号"
                                        className="p-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/10 transition text-xs font-semibold"
                                    >
                                        {fontSizeLevel === -1 ? 'A-' : fontSizeLevel === 1 ? 'A+' : 'A'}
                                    </button>
                                    <button
                                        onClick={handleCopy}
                                        title="复制 LRC 歌词"
                                        className="p-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/10 transition relative"
                                    >
                                        {copied ? <Check size={16} className="text-emerald-400" /> : <Copy size={16} />}
                                    </button>
                                    <button
                                        onClick={handlePolish}
                                        title="AI 润色（修复错别字与标点）"
                                        disabled={polishing}
                                        className={`p-2 rounded-lg bg-indigo-500/20 text-indigo-300 hover:bg-indigo-500 hover:text-white transition ${polishing ? 'animate-spin' : ''}`}
                                    >
                                        <Sparkles size={16} />
                                    </button>
                                    <button
                                        onClick={() => setIsEditing(true)}
                                        title="手动编辑/粘贴歌词"
                                        className="p-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/10 transition"
                                    >
                                        <Edit3 size={16} />
                                    </button>
                                </>
                            )}
                            <button
                                onClick={onClose}
                                title="关闭抽屉"
                                className="p-2 hover:bg-white/10 rounded-lg transition text-gray-400 hover:text-white"
                            >
                                <X size={18} />
                            </button>
                        </div>
                    </div>

                    {/* Drawer Content */}
                    <div 
                        className="flex-1 overflow-y-auto relative p-4 select-none scroll-smooth"
                        ref={scrollContainerRef}
                        onScroll={handleContainerScroll}
                    >
                        {loading ? (
                            <div className="flex flex-col justify-center items-center h-full text-gray-400 space-y-3">
                                <RefreshCw className="animate-spin text-purple-400" size={28} />
                                <span className="text-sm">正在加载歌词...</span>
                            </div>
                        ) : isEditing ? (
                            /* Manual Edit View */
                            <div className="flex flex-col h-full space-y-3">
                                <div className="flex justify-between items-center text-xs text-gray-400">
                                    <span>编辑或粘贴 LRC 歌词内容：</span>
                                    <span>包含 [00:00.00] 时间戳</span>
                                </div>
                                <textarea
                                    className="flex-1 w-full bg-gray-950/80 border border-gray-700/60 rounded-xl p-3 font-mono text-xs text-gray-200 outline-none focus:border-purple-500 focus:ring-1 focus:ring-purple-500 resize-none leading-relaxed"
                                    value={editText}
                                    onChange={(e) => setEditText(e.target.value)}
                                    placeholder="[00:00.00] 歌词第一行&#10;[00:05.00] 歌词第二行..."
                                />
                                <div className="flex justify-end gap-2 pt-2">
                                    <button
                                        onClick={() => setIsEditing(false)}
                                        className="px-4 py-2 rounded-lg bg-gray-800 hover:bg-gray-700 text-gray-300 text-xs font-medium transition"
                                    >
                                        取消
                                    </button>
                                    <button
                                        onClick={handleSaveManualEdit}
                                        disabled={savingEdit}
                                        className="px-5 py-2 rounded-lg bg-gradient-to-r from-purple-600 to-indigo-600 hover:from-purple-500 hover:to-indigo-500 text-white text-xs font-medium transition shadow-lg shadow-purple-900/30 flex items-center gap-1.5"
                                    >
                                        {savingEdit ? <RefreshCw size={14} className="animate-spin" /> : <Check size={14} />}
                                        保存歌词
                                    </button>
                                </div>
                            </div>
                        ) : lyricData && parsedLines.length > 0 ? (
                            /* Synchronized Lyrics View */
                            <div className="space-y-6 py-12 px-2">
                                {parsedLines.map((line, idx) => {
                                    const isActive = idx === activeIndex;
                                    const isPast = activeIndex !== -1 && idx < activeIndex;
                                    
                                    let textSizeClass = 'text-base';
                                    if (fontSizeLevel === -1) textSizeClass = isActive ? 'text-lg' : 'text-sm';
                                    else if (fontSizeLevel === 1) textSizeClass = isActive ? 'text-2xl' : 'text-lg';
                                    else textSizeClass = isActive ? 'text-xl' : 'text-base';

                                    return (
                                        <div
                                            key={idx}
                                            ref={isActive ? activeLineRef : null}
                                            onClick={() => onSeek && onSeek(line.time)}
                                            className={`text-center transition-all duration-300 cursor-pointer rounded-xl py-1.5 px-3 group
                                                ${isActive
                                                    ? `${textSizeClass} font-bold text-white scale-105 bg-purple-500/10 shadow-lg shadow-purple-900/20 backdrop-blur-sm border border-purple-500/20`
                                                    : isPast
                                                        ? `${textSizeClass} text-gray-400/80 hover:text-gray-200 hover:bg-white/5`
                                                        : `${textSizeClass} text-gray-500/80 hover:text-gray-300 hover:bg-white/5`
                                                }`}
                                        >
                                            <span className={isActive ? 'bg-clip-text text-transparent bg-gradient-to-r from-purple-200 via-white to-indigo-200' : ''}>
                                                {line.text}
                                            </span>
                                        </div>
                                    );
                                })}

                                <div className="pt-12 text-[11px] text-center text-gray-600 flex items-center justify-center gap-2">
                                    <span>来源：{lyricData.source}</span>
                                    {lyricData.language && <span>• 语言: {lyricData.language}</span>}
                                </div>
                            </div>
                        ) : (
                            /* No Lyrics / AI Generation State */
                            <div className="flex flex-col items-center justify-center h-full space-y-6 px-4">
                                {generating ? (
                                    /* Rich AI Listening Animation */
                                    <div className="flex flex-col items-center space-y-5 text-center max-w-xs">
                                        <div className="relative flex items-center justify-center w-20 h-20">
                                            <div className="absolute inset-0 rounded-full bg-purple-600/20 animate-ping" />
                                            <div className="absolute inset-2 rounded-full bg-gradient-to-tr from-purple-600 to-indigo-600 opacity-80 blur-sm animate-pulse" />
                                            <div className="relative w-14 h-14 rounded-full bg-gray-900 border border-purple-500/40 flex items-center justify-center text-purple-300 shadow-xl">
                                                <Mic2 size={26} className="animate-bounce" />
                                            </div>
                                        </div>

                                        {/* Audio Wave Visualizer */}
                                        <div className="flex items-center gap-1.5 h-8">
                                            {[0.4, 0.8, 1.0, 0.6, 0.9, 0.5, 0.7].map((height, i) => (
                                                <div
                                                    key={i}
                                                    className="w-1 bg-gradient-to-t from-purple-500 to-indigo-400 rounded-full animate-pulse"
                                                    style={{
                                                        height: `${height * 100}%`,
                                                        animationDelay: `${i * 150}ms`,
                                                        animationDuration: '800ms'
                                                    }}
                                                />
                                            ))}
                                        </div>

                                        <div className="space-y-1.5">
                                            <h3 className="text-sm font-semibold text-white">AI 正在聆听转录中...</h3>
                                            <p className="text-xs text-purple-300/80">
                                                已用时 {elapsedSeconds} 秒 <span className="text-gray-500">（通常耗时约 30~45 秒）</span>
                                            </p>
                                            <p className="text-[11px] text-gray-400 pt-2 leading-relaxed">
                                                Whisper 正在读取 NAS 音频并精准计算毫秒级时间戳，完成后将自动刷新展现。
                                            </p>
                                        </div>
                                    </div>
                                ) : (
                                    /* No Lyrics Action Card */
                                    <div className="flex flex-col items-center text-center space-y-5 w-full max-w-sm">
                                        <div className="w-16 h-16 rounded-2xl bg-gray-800/80 border border-gray-700/50 flex items-center justify-center text-gray-400 shadow-inner">
                                            <Volume2 size={30} />
                                        </div>

                                        <div className="space-y-1">
                                            <h3 className="text-base font-semibold text-white">暂无同步歌词</h3>
                                            <p className="text-xs text-gray-400">
                                                当前曲目尚未生成 LRC 歌词，可通过本地 AI 一键识别提取
                                            </p>
                                        </div>

                                        {aiAvailable ? (
                                            <div className="w-full space-y-3 bg-gray-800/40 p-4 rounded-2xl border border-white/5">
                                                <div className="grid grid-cols-2 gap-2 text-left">
                                                    <div>
                                                        <label className="text-[10px] text-gray-400 mb-1 block">指定语言</label>
                                                        <select
                                                            className="w-full bg-gray-900 border border-gray-700 text-white text-xs rounded-lg p-2 focus:ring-1 focus:ring-purple-500 outline-none"
                                                            value={lang}
                                                            onChange={(e) => setLang(e.target.value)}
                                                        >
                                                            <option value="">自动检测 (Auto)</option>
                                                            <option value="zh">中文 (Chinese)</option>
                                                            <option value="en">英语 (English)</option>
                                                            <option value="ja">日语 (Japanese)</option>
                                                            <option value="ko">韩语 (Korean)</option>
                                                            <option value="yue">粤语 (Cantonese)</option>
                                                        </select>
                                                    </div>
                                                    <div>
                                                        <label className="text-[10px] text-gray-400 mb-1 block">提示词 (可选)</label>
                                                        <input
                                                            type="text"
                                                            className="w-full bg-gray-900 border border-gray-700 text-white text-xs rounded-lg p-2 focus:ring-1 focus:ring-purple-500 outline-none"
                                                            placeholder="如: 繁體中文"
                                                            value={customPrompt}
                                                            onChange={(e) => setCustomPrompt(e.target.value)}
                                                        />
                                                    </div>
                                                </div>

                                                <button
                                                    onClick={handleGenerate}
                                                    className="w-full py-2.5 bg-gradient-to-r from-purple-600 via-indigo-600 to-blue-600 hover:from-purple-500 hover:to-blue-500 text-white text-xs font-semibold rounded-xl transition shadow-lg shadow-purple-900/30 flex items-center justify-center gap-2 active:scale-[0.98]"
                                                >
                                                    <Sparkles size={15} />
                                                    ✨ 启动 AI 智能识别歌词
                                                </button>

                                                <button
                                                    onClick={() => setIsEditing(true)}
                                                    className="w-full py-2 bg-gray-800 hover:bg-gray-700 text-gray-300 text-xs rounded-xl transition font-medium flex items-center justify-center gap-1.5"
                                                >
                                                    <Edit3 size={13} />
                                                    手动粘贴 / 输入歌词
                                                </button>
                                            </div>
                                        ) : (
                                            <div className="w-full space-y-2">
                                                <div className="text-xs text-amber-400 bg-amber-500/10 border border-amber-500/20 px-3 py-2 rounded-xl">
                                                    AI 服务离线（可手动粘贴歌词）
                                                </div>
                                                <button
                                                    onClick={() => setIsEditing(true)}
                                                    className="w-full py-2.5 bg-gray-800 hover:bg-gray-700 text-white text-xs rounded-xl transition font-medium flex items-center justify-center gap-1.5"
                                                >
                                                    <Edit3 size={14} />
                                                    手动粘贴 / 输入歌词
                                                </button>
                                            </div>
                                        )}

                                        {error && (
                                            <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 px-3 py-2 rounded-xl text-center w-full">
                                                {error}
                                            </div>
                                        )}
                                    </div>
                                )}
                            </div>
                        )}

                        {/* Floating button to resume auto-scroll */}
                        {userScrolled && lyricData && parsedLines.length > 0 && !isEditing && (
                            <button
                                onClick={scrollToCurrentLine}
                                className="sticky bottom-4 left-1/2 transform -translate-x-1/2 px-3 py-1.5 bg-purple-600/90 hover:bg-purple-600 text-white text-xs rounded-full shadow-xl shadow-purple-900/40 backdrop-blur-md transition flex items-center gap-1.5 border border-purple-400/30 animate-bounce"
                            >
                                <ArrowDownCircle size={14} />
                                回到当前歌词
                            </button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
};

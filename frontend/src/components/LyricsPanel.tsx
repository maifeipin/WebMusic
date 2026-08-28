import React, { useEffect, useState, useRef } from 'react';
import { getLyrics, generateLyrics, getAiStatus, optimizeLyrics, saveLyrics, deleteLyrics, getPlugins, api, type Lyric } from '../services/api';
import { 
    Sparkles, Copy, Check, Edit3, X, Mic2, Music, RefreshCw, Volume2, 
    ArrowDownCircle, Disc, Search, ChevronRight, Eye, CheckCircle2,
    Minimize2, Maximize2, Trash2, RotateCcw
} from 'lucide-react';

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

interface NeteaseSearchResult {
    id: number;
    name: string;
    artists?: { name: string }[];
    ar?: { name: string }[];
    album?: { name: string; picUrl?: string };
    al?: { name: string; picUrl?: string };
    duration?: number;
    dt?: number;
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

const formatDuration = (ms?: number) => {
    if (!ms) return '';
    const totalSec = Math.floor(ms / 1000);
    const m = Math.floor(totalSec / 60);
    const s = totalSec % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
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

    // Drawer state: expanded, collapsed (mini bar), open/close animation
    const [isCollapsed, setIsCollapsed] = useState(false);
    const [drawerOpen, setDrawerOpen] = useState(false);

    // Netease Plugin & Preview State
    const [neteasePluginId, setNeteasePluginId] = useState<number | null>(null);
    const [matchingNetease, setMatchingNetease] = useState(false);
    const [showNeteaseSearch, setShowNeteaseSearch] = useState(false);
    const [neteaseQuery, setNeteaseQuery] = useState('');
    const [neteaseResults, setNeteaseResults] = useState<NeteaseSearchResult[]>([]);
    const [searchingNetease, setSearchingNetease] = useState(false);

    // Live Lyric Preview in Netease View
    const [previewSong, setPreviewSong] = useState<NeteaseSearchResult | null>(null);
    const [previewLrcText, setPreviewLrcText] = useState<string | null>(null);
    const [loadingPreview, setLoadingPreview] = useState(false);

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
        // Trigger drawer entrance animation
        const timer = setTimeout(() => setDrawerOpen(true), 10);
        return () => {
            isMounted.current = false;
            clearTimeout(timer);
        };
    }, []);

    // Initial Load & AI/Plugin Health Check
    useEffect(() => {
        loadLyrics();
        checkAi();
        checkNeteasePlugin();
        setIsEditing(false);
        setShowNeteaseSearch(false);
        setPreviewSong(null);
        setPreviewLrcText(null);
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
        if (!userScrolled && activeLineRef.current && scrollContainerRef.current && !isEditing && !isCollapsed) {
            activeLineRef.current.scrollIntoView({
                behavior: 'smooth',
                block: 'center',
            });
        }
    }, [currentTime, userScrolled, isEditing, isCollapsed]);

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

    const handleCloseDrawer = () => {
        setDrawerOpen(false);
        setTimeout(onClose, 250);
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

    const checkNeteasePlugin = async () => {
        try {
            const plugins = await getPlugins();
            const found = plugins.find(p => p.baseUrl && p.isEnabled && (p.name.toLowerCase().includes("netease") || p.name.includes("网易")));
            if (isMounted.current && found) {
                setNeteasePluginId(found.id);
            }
        } catch { }
    };

    // Open Netease Search / Match View
    const handleOpenNeteaseView = (initialKeyword?: string) => {
        const query = (initialKeyword || `${song?.title || ''} ${song?.artist || ''}`).trim();
        setNeteaseQuery(query);
        setShowNeteaseSearch(true);
        setPreviewSong(null);
        setPreviewLrcText(null);
        if (query) {
            triggerNeteaseSearch(query);
        }
    };

    const triggerNeteaseSearch = async (queryText: string) => {
        if (!neteasePluginId || !queryText.trim()) return;
        setSearchingNetease(true);
        setError(null);
        try {
            const res = await api.get(`/plugins/${neteasePluginId}/proxy/search?keywords=${encodeURIComponent(queryText.trim())}`);
            const list: NeteaseSearchResult[] = res.data?.result?.songs || [];
            setNeteaseResults(list);
            if (list.length > 0) {
                // Auto preview top candidate
                handlePreviewSongLyric(list[0]);
            } else {
                setPreviewSong(null);
                setPreviewLrcText(null);
            }
        } catch {
            setNeteaseResults([]);
            setError("搜索网易云歌曲失败，请检查网络或插件。");
        } finally {
            if (isMounted.current) setSearchingNetease(false);
        }
    };

    // Preview lyric for a selected song candidate
    const handlePreviewSongLyric = async (candidate: NeteaseSearchResult) => {
        if (!neteasePluginId) return;
        setPreviewSong(candidate);
        setLoadingPreview(true);
        setPreviewLrcText(null);
        try {
            const res = await api.get(`/plugins/${neteasePluginId}/proxy/lyric?id=${candidate.id}`);
            const lrc = res.data?.lrc?.lyric;
            if (isMounted.current) {
                setPreviewLrcText(lrc && lrc.trim() !== '' ? lrc : '（该版本无歌词内容）');
            }
        } catch {
            if (isMounted.current) {
                setPreviewLrcText('（加载歌词预览失败）');
            }
        } finally {
            if (isMounted.current) setLoadingPreview(false);
        }
    };

    // Apply previewed or selected lyric to DB
    const handleApplyLyric = async (lrcContent: string) => {
        if (!lrcContent || lrcContent.startsWith('（')) return;
        setMatchingNetease(true);
        setError(null);
        try {
            await saveLyrics(mediaId, lrcContent, '网易云音乐 (官方LRC)', 'netease');
            if (isMounted.current) {
                setLyricData({
                    id: lyricData?.id || 0,
                    content: lrcContent,
                    language: 'zh',
                    source: '网易云音乐 (官方LRC)',
                    version: 'netease',
                    Title: song?.title || lyricData?.Title,
                    Artist: song?.artist || lyricData?.Artist
                });
                setShowNeteaseSearch(false);
                setPreviewSong(null);
                setPreviewLrcText(null);
            }
        } catch {
            setError("保存歌词失败，请重试。");
        } finally {
            if (isMounted.current) setMatchingNetease(false);
        }
    };

    // AI Generation with intelligent polling fallback
    const handleGenerate = async () => {
        setGenerating(true);
        setError(null);

        let pollTimer: any = null;
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
            } catch { }
        }, 3000);

        try {
            const data = await generateLyrics(mediaId, lang, customPrompt);
            isDone = true;
            stopPolling();
            if (isMounted.current) {
                setLyricData(data);
            }
        } catch {
            if (!isDone) {
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

    const handleDeleteLyrics = async () => {
        if (!confirm("确定要删除当前歌曲的歌词吗？删除后可随时重新匹配。")) return;
        setLoading(true);
        setError(null);
        try {
            await deleteLyrics(mediaId);
            if (isMounted.current) {
                setLyricData(null);
                setParsedLines([]);
                setEditText('');
                setIsEditing(false);
                setShowNeteaseSearch(false);
            }
        } catch {
            if (isMounted.current) setError("删除歌词失败，请重试。");
        } finally {
            if (isMounted.current) setLoading(false);
        }
    };

    const handleSaveManualEdit = async () => {
        setSavingEdit(true);
        setError(null);
        try {
            if (!editText.trim()) {
                // User emptied content -> delete lyric from DB
                await deleteLyrics(mediaId);
                if (isMounted.current) {
                    setLyricData(null);
                    setParsedLines([]);
                    setEditText('');
                    setIsEditing(false);
                }
                return;
            }

            await saveLyrics(mediaId, editText, 'User Edited', 'manual');
            if (isMounted.current) {
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
            }
        } catch {
            if (isMounted.current) setError("保存歌词失败，请重试。");
        } finally {
            if (isMounted.current) setSavingEdit(false);
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

    const currentLineText = activeIndex >= 0 && parsedLines[activeIndex] ? parsedLines[activeIndex].text : '';
    const titleText = song?.title || lyricData?.Title || '歌词面板';
    const artistText = song?.artist || lyricData?.Artist || 'Unknown Artist';

    return (
        <div className="fixed inset-0 z-[80] pointer-events-none">
            {/* Backdrop: only visible when not collapsed */}
            <div 
                className={`fixed inset-0 bg-black/60 backdrop-blur-sm transition-opacity duration-300 pointer-events-auto ${
                    drawerOpen && !isCollapsed ? 'opacity-100' : 'opacity-0 pointer-events-none'
                }`}
                onClick={handleCloseDrawer}
            />

            {/* Collapsed Mini Floating Lyric Bar (Docked at top/bottom-right) */}
            {isCollapsed && (
                <div 
                    onClick={() => setIsCollapsed(false)}
                    className="fixed bottom-24 right-6 pointer-events-auto bg-gray-900/90 hover:bg-gray-800/95 backdrop-blur-xl border border-purple-500/40 text-white rounded-2xl shadow-2xl p-3 max-w-sm sm:max-w-md flex items-center gap-3 cursor-pointer transition-all duration-300 hover:scale-105 group animate-fade-in"
                >
                    <div className="w-8 h-8 rounded-xl bg-purple-600/30 border border-purple-500/30 flex items-center justify-center text-purple-300 flex-shrink-0">
                        <Music size={16} className="animate-pulse" />
                    </div>
                    <div className="min-w-0 flex-1">
                        <div className="text-[10px] text-gray-400 truncate flex items-center gap-1.5">
                            <span className="font-semibold text-white truncate">{titleText}</span>
                            <span>•</span>
                            <span className="truncate">{artistText}</span>
                        </div>
                        <div className="text-xs font-bold text-purple-200 truncate mt-0.5">
                            {currentLineText || '♪ 正在播放...'}
                        </div>
                    </div>
                    <button 
                        title="展开抽屉面板"
                        className="p-1.5 rounded-lg bg-white/10 hover:bg-white/20 text-gray-300 hover:text-white transition"
                    >
                        <Maximize2 size={14} />
                    </button>
                </div>
            )}

            {/* Sliding Drawer Container */}
            <div 
                className={`fixed inset-y-0 right-0 max-w-full flex pl-6 sm:pl-10 pointer-events-auto transition-transform duration-300 ease-out ${
                    drawerOpen && !isCollapsed ? 'translate-x-0' : 'translate-x-full'
                }`}
            >
                {/* Left Edge Collapse / Expand Handle Toggle Button */}
                <button
                    onClick={() => setIsCollapsed(true)}
                    title="收起为浮动小窗"
                    className="self-center -ml-4 w-7 h-14 bg-gray-800/90 hover:bg-gray-700 text-gray-400 hover:text-white rounded-l-xl border-l border-y border-white/10 shadow-2xl flex items-center justify-center transition backdrop-blur-md z-10"
                >
                    <ChevronRight size={18} />
                </button>

                <div className="w-screen max-w-full sm:max-w-md md:max-w-lg lg:max-w-xl bg-gray-900/95 backdrop-blur-2xl border-l border-white/10 shadow-2xl flex flex-col h-full overflow-hidden">
                    
                    {/* Drawer Header */}
                    <div className="p-4 border-b border-white/10 bg-gray-900/80 backdrop-blur-md flex items-center justify-between gap-3 flex-shrink-0">
                        <div className="flex items-center gap-3 overflow-hidden flex-1">
                            <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-purple-600/30 to-indigo-600/30 border border-purple-500/20 flex items-center justify-center flex-shrink-0 text-purple-400 shadow-inner">
                                <Music size={20} />
                            </div>
                            <div className="flex flex-col min-w-0">
                                <div className="flex items-center gap-2">
                                    <h2 className="text-sm font-bold text-white truncate max-w-[180px] sm:max-w-[220px]">
                                        {titleText}
                                    </h2>
                                    {lyricData?.source && (
                                        <span className="text-[10px] px-1.5 py-0.5 rounded-full bg-purple-500/20 text-purple-300 border border-purple-500/30 font-medium whitespace-nowrap">
                                            {lyricData.source.includes('Whisper') ? 'Whisper AI' : lyricData.source.includes('网易云') ? '网易云官方' : lyricData.source.includes('Gemini') ? 'Gemini 润色' : lyricData.source}
                                        </span>
                                    )}
                                </div>
                                <span className="text-xs text-gray-400 truncate">{artistText}</span>
                            </div>
                        </div>

                        {/* Top Actions */}
                        <div className="flex items-center gap-1.5 flex-shrink-0">
                            {lyricData && !isEditing && !showNeteaseSearch && (
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
                                        onClick={() => handleOpenNeteaseView()}
                                        title="重新匹配网易云歌词 / 更换版本"
                                        className="p-2 rounded-lg text-rose-400/90 hover:text-rose-300 hover:bg-rose-500/10 transition"
                                    >
                                        <RotateCcw size={16} />
                                    </button>
                                    <button
                                        onClick={() => setIsEditing(true)}
                                        title="手动编辑/粘贴歌词"
                                        className="p-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/10 transition"
                                    >
                                        <Edit3 size={16} />
                                    </button>
                                    <button
                                        onClick={handleDeleteLyrics}
                                        title="删除当前歌曲歌词"
                                        className="p-2 rounded-lg text-rose-400/70 hover:text-rose-300 hover:bg-rose-500/20 transition"
                                    >
                                        <Trash2 size={16} />
                                    </button>
                                </>
                            )}
                            <button
                                onClick={() => setIsCollapsed(true)}
                                title="收起抽屉"
                                className="p-2 hover:bg-white/10 rounded-lg transition text-gray-400 hover:text-white"
                            >
                                <Minimize2 size={16} />
                            </button>
                            <button
                                onClick={handleCloseDrawer}
                                title="关闭"
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
                                <span className="text-sm">正在处理歌词...</span>
                            </div>
                        ) : isEditing ? (
                            /* Manual Edit View */
                            <div className="flex flex-col h-full space-y-3">
                                <div className="flex justify-between items-center text-xs text-gray-400">
                                    <span>编辑或粘贴 LRC 歌词内容（清空保存即可删除）：</span>
                                    <span>包含 [00:00.00] 时间戳</span>
                                </div>
                                <textarea
                                    className="flex-1 w-full bg-gray-950/80 border border-gray-700/60 rounded-xl p-3 font-mono text-xs text-gray-200 outline-none focus:border-purple-500 focus:ring-1 focus:ring-purple-500 resize-none leading-relaxed"
                                    value={editText}
                                    onChange={(e) => setEditText(e.target.value)}
                                    placeholder="[00:00.00] 歌词第一行&#10;[00:05.00] 歌词第二行..."
                                />
                                <div className="flex items-center justify-between gap-2 pt-2">
                                    <button
                                        onClick={handleDeleteLyrics}
                                        className="px-3 py-2 rounded-lg bg-rose-500/10 hover:bg-rose-500/20 text-rose-300 text-xs font-medium transition flex items-center gap-1.5"
                                    >
                                        <Trash2 size={13} />
                                        删除歌词
                                    </button>
                                    <div className="flex items-center gap-2">
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
                            </div>
                        ) : showNeteaseSearch ? (
                            /* Netease Search, Candidate List & Live Lyric Preview View */
                            <div className="flex flex-col h-full space-y-3">
                                <div className="flex items-center justify-between">
                                    <div className="flex items-center gap-2 text-rose-400 font-semibold text-sm">
                                        <Disc size={18} />
                                        <span>网易云官方歌词匹配与预览</span>
                                    </div>
                                    <button
                                        onClick={() => setShowNeteaseSearch(false)}
                                        className="text-xs text-gray-400 hover:text-white px-2 py-1 rounded bg-white/5"
                                    >
                                        返回歌词
                                    </button>
                                </div>

                                {/* Search Bar */}
                                <div className="flex gap-2">
                                    <input
                                        type="text"
                                        className="flex-1 bg-gray-950 border border-gray-700 text-white text-xs rounded-xl px-3 py-2 outline-none focus:border-rose-500"
                                        placeholder="搜索歌曲名 / 歌手..."
                                        value={neteaseQuery}
                                        onChange={(e) => setNeteaseQuery(e.target.value)}
                                        onKeyDown={(e) => e.key === 'Enter' && triggerNeteaseSearch(neteaseQuery)}
                                    />
                                    <button
                                        onClick={() => triggerNeteaseSearch(neteaseQuery)}
                                        disabled={searchingNetease}
                                        className="px-4 py-2 bg-rose-600 hover:bg-rose-500 text-white text-xs font-medium rounded-xl transition flex items-center gap-1"
                                    >
                                        {searchingNetease ? <RefreshCw size={14} className="animate-spin" /> : <Search size={14} />}
                                        搜索
                                    </button>
                                </div>

                                {/* Candidate Results & Live Preview Dual Pane */}
                                <div className="flex-1 grid grid-rows-2 gap-3 min-h-0">
                                    {/* Top: Candidate List */}
                                    <div className="bg-gray-950/60 rounded-xl border border-white/5 p-2 overflow-y-auto space-y-1.5">
                                        <div className="text-[11px] text-gray-400 px-2 py-1 font-medium flex justify-between">
                                            <span>匹配候选（点击条目预览歌词）：</span>
                                            {neteaseResults.length > 0 && <span>共 {neteaseResults.length} 条</span>}
                                        </div>

                                        {searchingNetease ? (
                                            <div className="flex justify-center items-center py-8 text-gray-400 text-xs gap-2">
                                                <RefreshCw size={16} className="animate-spin text-rose-400" />
                                                正在检索网易云曲库...
                                            </div>
                                        ) : neteaseResults.length > 0 ? (
                                            neteaseResults.map((s) => {
                                                const ar = (s.artists || s.ar || []).map(a => a.name).join(', ');
                                                const al = (s.album || s.al)?.name;
                                                const isSelected = previewSong?.id === s.id;
                                                const durationStr = formatDuration(s.duration || s.dt);

                                                return (
                                                    <div
                                                        key={s.id}
                                                        onClick={() => handlePreviewSongLyric(s)}
                                                        className={`p-2.5 rounded-xl cursor-pointer transition flex items-center justify-between group border ${
                                                            isSelected 
                                                                ? 'bg-rose-500/20 border-rose-500/40 text-white shadow-md' 
                                                                : 'bg-gray-800/40 hover:bg-gray-800 border-transparent text-gray-300'
                                                        }`}
                                                    >
                                                        <div className="min-w-0 flex-1 pr-2">
                                                            <div className="text-xs font-bold truncate flex items-center gap-1.5">
                                                                <span className={isSelected ? 'text-rose-300' : 'group-hover:text-white'}>{s.name}</span>
                                                                {durationStr && <span className="text-[10px] text-gray-500 font-normal">({durationStr})</span>}
                                                            </div>
                                                            <div className="text-[11px] text-gray-400 truncate mt-0.5">
                                                                {ar} {al ? `• ${al}` : ''}
                                                            </div>
                                                        </div>
                                                        <div className="flex items-center gap-2">
                                                            {isSelected && (
                                                                <span className="text-[10px] bg-rose-500 text-white px-2 py-0.5 rounded-full font-medium">
                                                                    预览中
                                                                </span>
                                                            )}
                                                            <ChevronRight size={14} className="text-gray-500 group-hover:text-rose-400" />
                                                        </div>
                                                    </div>
                                                );
                                            })
                                        ) : (
                                            <div className="text-center text-xs text-gray-500 py-8">
                                                未找到匹配歌曲，请输入歌曲名称搜索
                                            </div>
                                        )}
                                    </div>

                                    {/* Bottom: Live Lyric Preview & Apply Action */}
                                    <div className="bg-gray-950/80 rounded-xl border border-white/5 p-3 flex flex-col min-h-0">
                                        <div className="flex items-center justify-between pb-2 border-b border-white/5">
                                            <div className="flex items-center gap-2 text-xs font-medium text-gray-300 truncate">
                                                <Eye size={14} className="text-purple-400" />
                                                <span>歌词实时预览:</span>
                                                {previewSong && <span className="text-rose-400 font-semibold truncate max-w-[150px]">{previewSong.name}</span>}
                                            </div>
                                            {previewLrcText && !previewLrcText.startsWith('（') && (
                                                <button
                                                    onClick={() => previewLrcText && handleApplyLyric(previewLrcText)}
                                                    disabled={matchingNetease}
                                                    className="px-3 py-1 bg-gradient-to-r from-emerald-600 to-teal-600 hover:from-emerald-500 hover:to-teal-500 text-white text-xs font-semibold rounded-lg shadow transition flex items-center gap-1"
                                                >
                                                    {matchingNetease ? <RefreshCw size={12} className="animate-spin" /> : <CheckCircle2 size={12} />}
                                                    确认采用此歌词
                                                </button>
                                            )}
                                        </div>

                                        <div className="flex-1 overflow-y-auto pt-2 font-mono text-[11px] text-gray-300 leading-relaxed whitespace-pre-wrap select-text pr-1">
                                            {loadingPreview ? (
                                                <div className="flex items-center justify-center h-full text-gray-500 gap-2">
                                                    <RefreshCw size={14} className="animate-spin" />
                                                    正在提取该版本歌词预览...
                                                </div>
                                            ) : previewLrcText ? (
                                                previewLrcText
                                            ) : (
                                                <div className="flex items-center justify-center h-full text-gray-600">
                                                    选择上方候选版本即可实时预览完整歌词
                                                </div>
                                            )}
                                        </div>
                                    </div>
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
                            /* No Lyrics / AI & Netease Action State */
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
                                                支持网易云官方歌词检索与实时预览，或使用本地 AI 声学模型自动识别
                                            </p>
                                        </div>

                                        <div className="w-full space-y-3 bg-gray-800/40 p-4 rounded-2xl border border-white/5">
                                            {/* Priority 1: Netease Match & Preview */}
                                            {neteasePluginId && (
                                                <div className="space-y-2">
                                                    <button
                                                        onClick={() => handleOpenNeteaseView()}
                                                        className="w-full py-2.5 bg-gradient-to-r from-rose-600 to-red-600 hover:from-rose-500 hover:to-red-500 text-white text-xs font-semibold rounded-xl transition shadow-lg shadow-rose-900/30 flex items-center justify-center gap-2 active:scale-[0.98]"
                                                    >
                                                        <Disc size={15} />
                                                        🔴 网易云匹配歌词（支持预览 / 选版本）
                                                    </button>
                                                </div>
                                            )}

                                            {/* Priority 2: AI Whisper Generation */}
                                            {aiAvailable ? (
                                                <div className="pt-2 border-t border-white/5 space-y-2.5">
                                                    <div className="grid grid-cols-2 gap-2 text-left">
                                                        <div>
                                                            <label className="text-[10px] text-gray-400 mb-1 block">AI 识别语言</label>
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
                                                        ✨ 启动 AI 本地模型听音识别 (~30s)
                                                    </button>
                                                </div>
                                            ) : (
                                                <div className="text-[11px] text-amber-400/80 bg-amber-500/10 border border-amber-500/20 px-3 py-1.5 rounded-xl">
                                                    AI 服务离线（建议使用网易云匹配）
                                                </div>
                                            )}

                                            <div className="pt-2 border-t border-white/5">
                                                <button
                                                    onClick={() => setIsEditing(true)}
                                                    className="w-full py-2 bg-gray-800 hover:bg-gray-700 text-gray-300 text-xs rounded-xl transition font-medium flex items-center justify-center gap-1.5"
                                                >
                                                    <Edit3 size={13} />
                                                    手动粘贴 / 输入歌词
                                                </button>
                                            </div>
                                        </div>

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
                        {userScrolled && lyricData && parsedLines.length > 0 && !isEditing && !showNeteaseSearch && (
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

import { useState, useEffect } from 'react';
import { Filter, Save, Trash2, X, Folder, CheckSquare, Square, RefreshCw, Zap, Sparkles, RotateCcw, Mic } from 'lucide-react';
import { api, suggestTags, applyTags, deleteMedia, startBatch, getBatchStatus, startLyricsBatch } from '../../services/api';

interface Song {
    id: number;
    title: string;
    artist: string;
    album: string;
    filePath: string;
    genre?: string;
    year?: number;
}

interface TagUpdate {
    id: number;
    title: string;
    artist: string;
    album: string;
    genre: string;
    year: number;
}

const PROMPT_TEMPLATES: Record<string, string> = {
    'Auto Magic': 'Analyze the filename and existing metadata. intelligently fix missing fields, correct capitalization, and remove garbage characters. Guess the Artist and Title if missing.',
    'From Filename': `Strictly extract Artist and Title from the filename.
1. Remove junk suffixes / prefixes like [mqms2], www.xxx.com, (Official Audio), etc.
2. If filename starts with a number (e.g. "01. Song"), remove the number prefix.
3. Common format is "Artist - Title". If separation is unclear, prioritize Title.`,
    'Fix Encoding': 'The metadata contains Mojibake (garbled text), likely GBK/GB2312 or Shift-JIS bytes interpreted as ISO-8859-1. Example: "º£À«Ìì¿Õ" corresponds to "海阔天空". Please recover the original correct characters (usually Chinese or Japanese) for Title, Artist, and Album.',
    'Genre Classifier': 'Based on the Artist and Album, predict the most appropriate Genre for these songs. Simplify to standard genres like Pop, Rock, Jazz, Classic, etc.'
};

export default function BatchProcessor() {
    const [candidates, setCandidates] = useState<Song[]>([]);
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const [criteria, setCriteria] = useState<string[]>([]);
    const [searchQuery, setSearchQuery] = useState('');
    const [loading, setLoading] = useState(false);
    const [pageSize, setPageSize] = useState(50);

    // AI State
    const [prompt, setPrompt] = useState(PROMPT_TEMPLATES['Auto Magic']);
    const [model, setModel] = useState('gemini-2.0-flash-lite-preview-02-05');
    const [results, setResults] = useState<TagUpdate[]>([]);
    const [processing, setProcessing] = useState(false);

    // Batch State
    const [batchId, setBatchId] = useState<string | null>(null);
    const [batchProgress, setBatchProgress] = useState<{ processed: number; total: number; success: number; failed: number; status: string } | null>(null);

    // Lyrics State
    const [lyricsLang, setLyricsLang] = useState('en');

    // Polling for Batch
    useEffect(() => {
        if (!batchId) return;
        const interval = setInterval(async () => {
            try {
                const res = await getBatchStatus(batchId);
                setBatchProgress(res.data);
                if (res.data.status === 'Completed' || res.data.status === 'Failed') {
                    clearInterval(interval);

                    if (res.data.status === 'Completed') {
                        setTimeout(() => {
                            alert(`Batch Job Finished!\nUpdated: ${res.data.success}\nFailed: ${res.data.failed}`);
                            setBatchId(null);
                            handleSearch();
                            setResults([]);
                            setSelectedIds(new Set());
                        }, 500);
                    } else {
                        setBatchId(null);
                        alert("Batch Job Failed");
                    }
                }
            } catch (e) {
                console.error("Polling error", e);
            }
        }, 1500);
        return () => clearInterval(interval);
    }, [batchId]);


    const handleSearch = async () => {
        setLoading(true);
        try {
            const params = new URLSearchParams();
            if (searchQuery) params.append('search', searchQuery);
            criteria.forEach(c => params.append('criteria', c));
            params.append('pageSize', pageSize.toString());

            const res = await api.get('/media', { params: params });
            // Map backend MediaFile to Song interface if needed, or use directly
            // Backend returns: id, title, artist, album, filePath...
            setCandidates(res.data.files);
        } catch (e) {
            console.error(e);
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        handleSearch();
    }, [pageSize]);

    const addCriteria = (c: string) => {
        if (!criteria.includes(c)) setCriteria([...criteria, c]);
    };

    const removeCriteria = (c: string) => {
        setCriteria(criteria.filter(item => item !== c));
    };

    const filterByPath = (filePath: string) => {
        const parts = filePath.split(/[\\/]/);
        parts.pop();
        const parentDir = parts.join('/');
        addCriteria(`filename:contains:${parentDir}`);
    };

    // Auto Template Logic
    useEffect(() => {
        if (criteria.some(c => c.includes('artist:isempty'))) {
            setPrompt(PROMPT_TEMPLATES['From Filename']);
        } else if (criteria.some(c => c.includes('genre:isempty'))) {
            setPrompt(PROMPT_TEMPLATES['Genre Classifier']);
        }
    }, [criteria]);

    const toggleSelect = (id: number) => {
        const next = new Set(selectedIds);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        setSelectedIds(next);
    };

    const handleGenerate = async () => {
        if (selectedIds.size === 0) return;
        setProcessing(true);
        setResults([]);
        try {
            const res = await suggestTags(Array.from(selectedIds), prompt, model);
            setResults(res.data);
        } catch (e) {
            alert("AI processing failed. Check console.");
        } finally {
            setProcessing(false);
        }
    };

    const handleStartBatch = async () => {
        if (selectedIds.size === 0) return;
        if (!confirm(`Start background batch processing for ${selectedIds.size} songs?\nThis will automatically apply changes.`)) return;

        try {
            const res = await startBatch(Array.from(selectedIds), prompt, model);
            setBatchId(res.data.batchId);
            setBatchProgress({ processed: 0, total: selectedIds.size, success: 0, failed: 0, status: 'Queued' });
        } catch (e: any) {
            if (e.response && e.response.data) alert(e.response.data);
            else alert("Failed to start batch.");
        }
    };

    // Update handleLyricsBatch to use state
    const handleLyricsBatch = async () => {
        if (selectedIds.size === 0) return;
        if (!confirm(`Generate lyrics for ${selectedIds.size} songs in [${lyricsLang.toUpperCase()}]?\nWARNING: This is slow (approx 10-30s per song).`)) return;

        try {
            const res = await startLyricsBatch(Array.from(selectedIds), false, lyricsLang);
            setBatchId(res.batchId);
            setBatchProgress({ processed: 0, total: selectedIds.size, success: 0, failed: 0, status: 'Queued' });
        } catch (e: any) {
            alert("Failed to start lyrics batch.");
            console.error(e);
        }
    };



    const handleApply = async () => {
        if (results.length === 0) return;
        if (!confirm(`Apply changes to ${results.length} songs?`)) return;

        setProcessing(true);
        try {
            const res = await applyTags(results);
            alert(`Successfully updated ${res.data.applied} songs!`);
            setResults([]);
            handleSearch(); // Refresh list
        } catch (e) {
            alert("Update failed");
        } finally {
            setProcessing(false);
        }
    };

    const discardResult = (id: number) => {
        setResults(prev => prev.filter(r => r.id !== id));
    };

    const handleDelete = async (id: number, force = false) => {
        if (!force && !confirm("Delete this song?")) return;

        try {
            await deleteMedia(id, force);
            setCandidates(prev => prev.filter(c => c.id !== id));
            setSelectedIds(prev => {
                const next = new Set(prev);
                next.delete(id);
                return next;
            });
        } catch (e: any) {
            if (e.response && e.response.status === 409) {
                const { details } = e.response.data;
                const msg = `This song is in use:\n- ${details.playlists} Playlists\n- ${details.history} History records\n- ${details.favorites} Favorites\n\nForce delete anyway?`;
                if (confirm(msg)) await handleDelete(id, true);
            } else {
                alert("Delete failed");
            }
        }
    };

    return (
        <div className="h-full flex flex-col md:flex-row divide-y md:divide-y-0 md:divide-x divide-gray-800">
            {/* Left: Input & Selection */}
            <div className="w-full md:w-5/12 flex flex-col p-4 bg-black/20">
                <div className="mb-4 space-y-3">
                    <h3 className="font-semibold text-gray-300 flex items-center gap-2">
                        <Filter size={18} /> Select Filter
                    </h3>

                    {/* Search Bar */}
                    <div className="flex gap-2">
                        <input
                            type="text"
                            className="flex-1 bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-blue-500 text-sm"
                            placeholder="General search..."
                            value={searchQuery}
                            onChange={(e) => setSearchQuery(e.target.value)}
                            onKeyDown={(e) => e.key === 'Enter' && handleSearch()}
                        />
                        <select
                            value={pageSize}
                            onChange={(e) => setPageSize(Number(e.target.value))}
                            className="bg-gray-800 border border-gray-700 rounded-lg px-2 text-white text-xs focus:outline-none focus:border-blue-500"
                            title="Items to fetch"
                        >
                            <option value={50}>50</option>
                            <option value={100}>100</option>
                            <option value={500}>500</option>
                            <option value={1000}>1K</option>
                        </select>
                        <button onClick={handleSearch} className="px-4 py-2 bg-blue-600 hover:bg-blue-500 rounded-lg text-white text-sm font-bold">Search</button>
                    </div>

                    {/* Quick Filters */}
                    <div className="flex flex-wrap gap-2">
                        <button onClick={() => addCriteria('artist:isempty')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ No Artist</button>
                        <button onClick={() => addCriteria('album:isempty')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ No Album</button>
                        <button onClick={() => addCriteria('genre:isempty')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ No Genre</button>
                        <button onClick={() => addCriteria('title:contains:track')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ "Track"</button>
                        <button onClick={() => addCriteria('encoding:is:mojibake')} className="text-xs bg-red-900/40 hover:bg-red-800/40 px-2 py-1 rounded text-red-300 border border-red-800/50" title="Find common encoding errors">+ 乱码 (Mojibake)</button>
                    </div>

                    {/* Active Chips */}
                    {criteria.length > 0 && (
                        <div className="flex flex-wrap gap-2 p-2 bg-black/40 rounded border border-gray-800 min-h-[40px]">
                            {criteria.map(c => (
                                <div key={c} className="bg-blue-900/40 text-blue-200 border border-blue-800 px-2 py-1 rounded text-xs flex items-center gap-2">
                                    <span>{c.replace(':', ' ')}</span>
                                    <button onClick={() => removeCriteria(c)} className="hover:text-white"><X size={12} /></button>
                                </div>
                            ))}
                        </div>
                    )}
                </div>

                <div className="flex-1 overflow-y-auto min-h-0 border border-gray-800 rounded-lg bg-black/40">
                    {loading ? (
                        <div className="p-4 text-center text-gray-500">Loading...</div>
                    ) : (
                        <table className="w-full text-left text-sm">
                            <thead className="bg-gray-800/50 text-gray-400 sticky top-0 backdrop-blur-sm z-10">
                                <tr>
                                    <th className="p-2 w-10 text-center">
                                        <button onClick={() => {
                                            if (selectedIds.size === candidates.length) setSelectedIds(new Set());
                                            else setSelectedIds(new Set(candidates.map(c => c.id)));
                                        }}>
                                            {candidates.length > 0 && selectedIds.size === candidates.length ? <CheckSquare size={16} /> : <Square size={16} />}
                                        </button>
                                    </th>
                                    <th className="p-2">Title</th>
                                    <th className="p-2">Artist</th>
                                    <th className="p-2">Filename</th>
                                    <th className="p-2 w-10"></th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-800/50">
                                {candidates.map(song => (
                                    <tr
                                        key={song.id}
                                        className={`hover:bg-white/5 cursor-pointer group ${selectedIds.has(song.id) ? 'bg-blue-500/10' : ''}`}
                                        onClick={() => toggleSelect(song.id)}
                                    >
                                        <td className="p-2 text-center">
                                            {selectedIds.has(song.id) ? <CheckSquare size={16} className="text-blue-400 mx-auto" /> : <Square size={16} className="text-gray-600 mx-auto" />}
                                        </td>
                                        <td className="p-2 truncate max-w-[120px] font-medium" title={song.title}>{song.title}</td>
                                        <td className="p-2 truncate max-w-[80px] text-gray-400">{song.artist}</td>
                                        <td className="p-2 truncate max-w-[120px] text-gray-500 text-xs font-mono flex items-center gap-2 group/file" title={song.filePath}>
                                            <button
                                                onClick={(e) => { e.stopPropagation(); filterByPath(song.filePath); }}
                                                className="p-1 hover:bg-white/10 rounded text-gray-600 hover:text-blue-400 opacity-0 group-hover/file:opacity-100 transition"
                                                title="Filter by this folder"
                                            >
                                                <Folder size={14} />
                                            </button>
                                            <span className="truncate">{song.filePath.split(/[\\/]/).pop()}</span>
                                        </td>
                                        <td className="p-2 w-10 text-center">
                                            <button
                                                onClick={(e) => { e.stopPropagation(); handleDelete(song.id); }}
                                                className="p-1.5 text-gray-600 hover:bg-red-900/30 hover:text-red-500 rounded transition opacity-0 group-hover:opacity-100"
                                                title="Delete Song"
                                            >
                                                <Trash2 size={16} />
                                            </button>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    )}
                </div>

                <div className="mt-4 text-sm text-gray-500">
                    Selected: {selectedIds.size} / {candidates.length}
                </div>
            </div>

            {/* Right: AI Actions */}
            <div className="w-full md:w-7/12 flex flex-col p-4 bg-gray-900/50 relative">
                {batchId && batchProgress && (
                    <div className="absolute inset-0 bg-black/80 z-50 flex items-center justify-center backdrop-blur-sm">
                        <div className="bg-gray-900 p-8 rounded-2xl border border-gray-700 max-w-md w-full text-center space-y-4">
                            <RefreshCw size={48} className="mx-auto text-purple-500 animate-spin" />
                            <h2 className="text-xl font-bold text-white">Batch Processing...</h2>
                            <p className="text-gray-400 text-sm">Gemini is analyzing and updating your library. <br /> This happens in the background.</p>

                            <div className="w-full bg-gray-800 rounded-full h-2 overflow-hidden">
                                <div
                                    className="bg-purple-500 h-full transition-all duration-500"
                                    style={{ width: `${(batchProgress.processed / batchProgress.total) * 100}%` }}
                                />
                            </div>
                            <div className="flex justify-between text-xs text-gray-500">
                                <span>Processed: {batchProgress.processed}/{batchProgress.total}</span>
                                <span>Success: {batchProgress.success}</span>
                            </div>
                        </div>
                    </div>
                )}

                <div className="mb-4">
                    <div className="flex justify-between items-center mb-2">
                        <h3 className="font-semibold text-gray-300 flex items-center gap-2">
                            <Sparkles size={18} className="text-purple-400" /> AI Instruction
                        </h3>
                        <div className="flex items-center gap-2">
                            <span className="text-xs text-gray-500">Model:</span>
                            <select
                                value={model}
                                onChange={(e) => setModel(e.target.value)}
                                className="bg-black/40 border border-gray-700 rounded text-xs px-2 py-1 text-gray-300 focus:outline-none focus:border-purple-500"
                            >
                                <option value="gemini-2.0-flash-lite-preview-02-05">Gemini 2.0 Flash-Lite (Batch)</option>
                                <option value="gemini-2.0-flash-exp">Gemini 2.0 Flash Exp (Fast)</option>
                                <option value="gemini-2.0-flash">Gemini 2.0 Flash (Stable)</option>
                            </select>
                        </div>
                    </div>

                    {/* Template Pills */}
                    <div className="flex flex-wrap gap-2 mb-2">
                        {Object.keys(PROMPT_TEMPLATES).map(key => (
                            <button
                                key={key}
                                onClick={() => setPrompt(PROMPT_TEMPLATES[key])}
                                className={`text-xs px-2 py-1 rounded border transition ${prompt === PROMPT_TEMPLATES[key] ? 'bg-purple-900/40 border-purple-500 text-purple-200' : 'bg-transparent border-gray-700 text-gray-400 hover:border-gray-500'}`}
                            >
                                {key}
                            </button>
                        ))}
                    </div>

                    <div className="flex gap-2">
                        <textarea
                            className="flex-1 h-20 bg-black/40 border border-gray-700 rounded-lg p-3 text-white focus:outline-none focus:border-purple-500 text-sm font-mono resize-none"
                            placeholder="e.g. Extract Artist and Title from the filename..."
                            value={prompt}
                            onChange={(e) => setPrompt(e.target.value)}
                        />
                        <div className="flex flex-col gap-2 w-24">
                            <button
                                onClick={handleGenerate}
                                disabled={selectedIds.size === 0 || selectedIds.size > 50 || processing}
                                className="flex-1 bg-purple-600 hover:bg-purple-500 disabled:bg-gray-800 disabled:text-gray-500 text-white rounded-lg font-bold flex flex-col items-center justify-center gap-1 transition text-xs disabled:cursor-not-allowed group/preview"
                                title={selectedIds.size > 50 ? "Limit 50 for preview. Use Auto Batch for more." : "Preview changes (Diff View)"}
                            >
                                {processing ? <div className="animate-spin w-5 h-5 border-2 border-white/30 border-t-white rounded-full" /> : <Sparkles size={20} />}
                                {processing ? 'Thinking' : 'Preview'}
                            </button>
                            <button
                                onClick={handleStartBatch}
                                disabled={selectedIds.size === 0 || processing}
                                className="h-8 bg-blue-600 hover:bg-blue-500 disabled:bg-gray-800 disabled:text-gray-500 text-white rounded-lg font-bold flex items-center justify-center gap-1 transition text-xs"
                                title="Auto-process in background (Up to 1000 items)"
                            >
                                <Zap size={14} /> Auto Tag
                            </button>
                        </div>
                    </div>

                    {/* Lyrics Section Separator */}
                    <div className="mt-3 pt-3 border-t border-gray-800 flex items-center justify-between">
                        <div className="flex items-center gap-2">
                            <Mic size={16} className="text-pink-500" />
                            <span className="text-sm font-semibold text-gray-300">Lyrics Gen</span>

                            <select
                                value={lyricsLang}
                                onChange={(e) => setLyricsLang(e.target.value)}
                                className="ml-2 bg-black/40 border border-gray-700 rounded text-xs px-2 py-1 text-gray-300 focus:outline-none focus:border-pink-500"
                            >
                                <option value="en">English (en)</option>
                                <option value="zh">Chinese (zh)</option>
                                <option value="ja">Japanese (ja)</option>
                                <option value="ko">Korean (ko)</option>
                                <option value="fr">French (fr)</option>
                                <option value="de">German (de)</option>
                                <option value="es">Spanish (es)</option>
                                <option value="ru">Russian (ru)</option>
                            </select>
                        </div>

                        <button
                            onClick={handleLyricsBatch}
                            disabled={selectedIds.size === 0 || processing}
                            className="w-24 h-8 bg-pink-600 hover:bg-pink-500 disabled:bg-gray-800 disabled:text-gray-500 text-white rounded-lg font-bold flex items-center justify-center gap-1 transition text-xs"
                            title="Generate Lyrics via Whisper"
                        >
                            <Mic size={14} /> Scan
                        </button>
                    </div>
                    {selectedIds.size > 50 && (
                        <div className="text-right text-xs text-orange-400 mt-1">
                            Selection &gt; 50: Preview disabled. Use Auto Batch.
                        </div>
                    )}
                </div>

                {/* Results Table */}
                <div className="flex-1 overflow-y-auto min-h-0 border border-gray-800 rounded-xl bg-black/40 relative">
                    {results.length === 0 ? (
                        <div className="absolute inset-0 flex items-center justify-center text-gray-600">
                            <div className="text-center">
                                <Sparkles size={48} className="mx-auto mb-2 opacity-20" />
                                <p>Select songs and click Preview or Auto Batch</p>
                            </div>
                        </div>
                    ) : (
                        <table className="w-full text-left text-sm">
                            <thead className="bg-gray-800/80 text-gray-400 sticky top-0 backdrop-blur-sm">
                                <tr>
                                    <th className="p-2 w-10"></th>
                                    <th className="p-2">Original</th>
                                    <th className="p-2 text-purple-400">Proposed Change</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-800/50">
                                {results.map(res => {
                                    const original = candidates.find(c => c.id === res.id);
                                    if (!original) return null;

                                    // Calculate Diffs
                                    const changes = [];
                                    if (res.title !== original.title) changes.push(`Title: ${res.title}`);
                                    if (res.artist !== original.artist) changes.push(`Artist: ${res.artist}`);
                                    if (res.album !== original.album) changes.push(`Album: ${res.album}`);
                                    if (res.genre !== original.genre) changes.push(`Genre: ${res.genre}`);
                                    if (res.year !== original.year && res.year !== 0) changes.push(`Year: ${res.year}`);

                                    return (
                                        <tr key={res.id} className="hover:bg-white/5 group">
                                            <td className="p-2 text-center">
                                                <button onClick={() => discardResult(res.id)} className="text-gray-600 hover:text-red-500 transition">
                                                    <X size={16} />
                                                </button>
                                            </td>
                                            <td className="p-2 w-1/3 opacity-50">
                                                <div className="font-medium truncate" title={original.title}>{original.title}</div>
                                                <div className="text-xs truncate">{original.artist}</div>
                                            </td>
                                            <td className="p-2 w-2/3">
                                                {changes.length > 0 ? (
                                                    <div className="space-y-1">
                                                        {changes.map((c, i) => (
                                                            <div key={i} className="text-purple-300 bg-purple-900/20 px-2 py-0.5 rounded w-fit text-xs font-mono border border-purple-500/30">
                                                                {c}
                                                            </div>
                                                        ))}
                                                    </div>
                                                ) : (
                                                    <span className="text-gray-600 italic">No change</span>
                                                )}
                                            </td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    )}
                </div>

                {/* Confirm Actions */}
                {results.length > 0 && (
                    <div className="mt-4 flex gap-3 justify-end">
                        <button
                            onClick={() => setResults([])}
                            className="px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-gray-300"
                        >
                            <RotateCcw size={16} className="inline mr-2" /> Discard
                        </button>
                        <button
                            onClick={handleApply}
                            disabled={processing}
                            className="px-6 py-2 bg-green-600 hover:bg-green-500 text-white rounded-lg font-bold shadow-lg shadow-green-900/20 flex items-center gap-2"
                        >
                            <Save size={18} /> Apply {results.length} Changes
                        </button>
                    </div>
                )}
            </div>
        </div>
    );
}

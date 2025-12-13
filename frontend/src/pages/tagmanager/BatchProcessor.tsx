import { useState, useEffect } from 'react';
import { Sparkles, Table, CheckSquare, Square, Save, X, RotateCcw, Filter, Folder } from 'lucide-react';
import { api, suggestTags, applyTags } from '../../services/api';

interface MediaItem {
    id: number;
    title: string;
    artist: string;
    album: string;
    genre: string;
    year: number;
    filePath: string;
}

const PROMPT_TEMPLATES: Record<string, string> = {
    'Auto Magic': 'Analyze the filename and existing metadata. intelligently fix missing fields, correct capitalization, and remove garbage characters. Guess the Artist and Title if missing.',
    'From Filename': `Strictly extract Artist and Title from the filename.
1. Remove junk suffixes/prefixes like [mqms2], www.xxx.com, (Official Audio), etc.
2. If filename starts with a number (e.g. "01. Song"), remove the number prefix.
3. Common format is "Artist - Title". If separation is unclear, prioritize Title.`,
    'Fix Encoding': 'The metadata likely has encoding issues (GBK/Shift-JIS decoded as UTF-8). Try to fix the garbled characters in Title and Artist.',
    'Genre Classifier': 'Based on the Artist and Album, predict the most appropriate Genre for these songs. Simplify to standard genres like Pop, Rock, Jazz, Classic, etc.'
};

export default function BatchProcessor() {
    // 1. Selection Phase
    const [searchQuery, setSearchQuery] = useState('');
    const [criteria, setCriteria] = useState<string[]>([]);
    const [candidates, setCandidates] = useState<MediaItem[]>([]);
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const [loading, setLoading] = useState(false);

    // 2. AI Phase
    const [model, setModel] = useState('gemini-2.0-flash-exp');
    const [prompt, setPrompt] = useState('Cleanup artist and title from filename.');
    const [aiResults, setAiResults] = useState<any[]>([]); // Diff
    const [processing, setProcessing] = useState(false);

    const handleSearch = async () => {
        setLoading(true);
        try {
            // Manual criteria params building for .NET compatibility (repeat keys)
            const params = new URLSearchParams();
            if (searchQuery) params.append('search', searchQuery);
            criteria.forEach(c => params.append('criteria', c));
            params.append('pageSize', '50');

            const res = await api.get('/media', { params: params });
            setCandidates(res.data.files);
        } catch (e) {
            console.error(e);
        } finally {
            setLoading(false);
        }
    };

    const addCriteria = (c: string) => {
        if (!criteria.includes(c)) {
            setCriteria([...criteria, c]);
        }
    };

    const removeCriteria = (c: string) => {
        setCriteria(criteria.filter(item => item !== c));
    };

    const filterByPath = (filePath: string) => {
        // Extract parent directory
        const parts = filePath.split(/[\\/]/);
        parts.pop(); // Remove filename
        const parentDir = parts.join('/');

        // Add path criteria
        // Ensure we handle Windows/Unix path diffs roughly by just searching string
        addCriteria(`filename:contains:${parentDir}`);
    };

    // Auto-Select Template based on Criteria
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
        setAiResults([]);
        try {
            const res = await suggestTags(Array.from(selectedIds), prompt, model);
            setAiResults(res.data);
        } catch (e) {
            alert("AI processing failed. Check console.");
            console.error(e);
        } finally {
            setProcessing(false);
        }
    };

    const handleApply = async () => {
        if (aiResults.length === 0) return;
        if (!confirm(`Apply changes to ${aiResults.length} songs?`)) return;

        setProcessing(true);
        try {
            const res = await applyTags(aiResults);
            alert(`Successfully updated ${res.data.applied} songs!`);
            setAiResults([]);
            handleSearch(); // Refresh list
        } catch (e) {
            alert("Update failed");
        } finally {
            setProcessing(false);
        }
    };

    const discardResult = (id: number) => {
        setAiResults(prev => prev.filter(r => r.id !== id));
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
                        <button onClick={handleSearch} className="px-4 py-2 bg-blue-600 hover:bg-blue-500 rounded-lg text-white text-sm font-bold">Search</button>
                    </div>

                    {/* Quick Filters */}
                    <div className="flex flex-wrap gap-2">
                        <button onClick={() => addCriteria('artist:isempty')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ No Artist</button>
                        <button onClick={() => addCriteria('album:isempty')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ No Album</button>
                        <button onClick={() => addCriteria('genre:isempty')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ No Genre</button>
                        <button onClick={() => addCriteria('title:contains:track')} className="text-xs bg-gray-700 hover:bg-gray-600 px-2 py-1 rounded text-gray-300 border border-gray-600">+ "Track"</button>
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
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-800/50">
                                {candidates.map(song => (
                                    <tr
                                        key={song.id}
                                        className={`hover:bg-white/5 cursor-pointer ${selectedIds.has(song.id) ? 'bg-blue-500/10' : ''}`}
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
            <div className="w-full md:w-7/12 flex flex-col p-4 bg-gray-900/50">
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
                                <option value="gemini-2.0-flash-exp">Gemini 2.0 Flash Exp (Fastest)</option>
                                <option value="gemini-2.0-flash">Gemini 2.0 Flash (Stable)</option>
                                <option value="gemini-2.0-flash-lite-preview-02-05">Gemini 2.0 Flash-Lite (Lightweight)</option>
                                <option value="gemini-1.5-flash">Gemini 1.5 Flash</option>
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
                        <button
                            onClick={handleGenerate}
                            disabled={selectedIds.size === 0 || processing}
                            className="w-24 bg-purple-600 hover:bg-purple-500 disabled:bg-gray-800 disabled:text-gray-500 text-white rounded-lg font-bold flex flex-col items-center justify-center gap-1 transition text-xs"
                        >
                            {processing ? <div className="animate-spin w-5 h-5 border-2 border-white/30 border-t-white rounded-full" /> : <Sparkles size={20} />}
                            {processing ? 'Thinking' : 'Generate'}
                        </button>
                    </div>
                </div>

                {/* Results Table */}
                <div className="flex-1 overflow-y-auto min-h-0 border border-gray-800 rounded-xl bg-black/40 relative">
                    {aiResults.length === 0 ? (
                        <div className="absolute inset-0 flex items-center justify-center text-gray-600">
                            <div className="text-center">
                                <Table size={48} className="mx-auto mb-2 opacity-20" />
                                <p>AI Suggestions will appear here</p>
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
                                {aiResults.map(res => {
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
                {aiResults.length > 0 && (
                    <div className="mt-4 flex gap-3 justify-end">
                        <button
                            onClick={() => setAiResults([])}
                            className="px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-gray-300"
                        >
                            <RotateCcw size={16} className="inline mr-2" /> Discard
                        </button>
                        <button
                            onClick={handleApply}
                            disabled={processing}
                            className="px-6 py-2 bg-green-600 hover:bg-green-500 text-white rounded-lg font-bold shadow-lg shadow-green-900/20 flex items-center gap-2"
                        >
                            <Save size={18} /> Apply {aiResults.length} Changes
                        </button>
                    </div>
                )}
            </div>
        </div>
    );
}

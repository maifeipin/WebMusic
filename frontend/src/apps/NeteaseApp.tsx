import { useState, useEffect } from 'react';
import { Search, Image as ImageIcon, Copy, ExternalLink, Disc, Loader2 } from 'lucide-react';
import api, { getPlugins } from '../services/api';

// Types for Netease API Response
interface NeteaseSong {
    id: number;
    name: string;
    artists: { name: string }[];
    album: { id: number; name: string; picUrl: string };
    duration: number;
}

interface NeteaseAlbum {
    id: number;
    name: string;
    picUrl: string;
    description: string;
    publishTime: number;
    company: string;
    artist: { name: string };
    songs: NeteaseSong[];
}

const NeteaseApp = () => {
    const [query, setQuery] = useState('');
    const [loading, setLoading] = useState(false);
    const [results, setResults] = useState<NeteaseSong[]>([]);
    const [selectedAlbum, setSelectedAlbum] = useState<NeteaseAlbum | null>(null);
    const [pluginId, setPluginId] = useState<number | null>(null);
    const [bbcode, setBbcode] = useState('');

    // Locate the Netease Plugin ID
    useEffect(() => {
        const init = async () => {
            const plugins = await getPlugins();
            // Try to find by typical names or URL
            const found = plugins.find(p => p.baseUrl && (p.name.includes("Netease") || p.name.includes("网易")));
            if (found) setPluginId(found.id);
        };
        init();
    }, []);

    const search = async () => {
        if (!pluginId || !query) return;
        setLoading(true);
        setResults([]);
        setSelectedAlbum(null);
        try {
            // Search API: /search?keywords=xxx
            const res = await api.get(`/plugins/${pluginId}/proxy/search?keywords=${encodeURIComponent(query)}`);
            if (res.data?.result?.songs) {
                setResults(res.data.result.songs);
            }
        } catch (err) {
            console.error(err);
            alert("Search failed. Ensure the Netease Plugin is running.");
        } finally {
            setLoading(false);
        }
    };

    const fetchAlbum = async (albumId: number) => {
        if (!pluginId) return;
        setLoading(true);
        try {
            // Album API: /album?id=xxx
            const res = await api.get(`/plugins/${pluginId}/proxy/album?id=${albumId}`);
            if (res.data?.album) {
                const albumData = res.data.album;
                const songs = res.data.songs || [];

                const fullAlbum: NeteaseAlbum = {
                    ...albumData,
                    songs: songs
                };
                setSelectedAlbum(fullAlbum);
                generateBBCode(fullAlbum);
            }
        } catch (err) {
            alert("Failed to fetch album details");
        } finally {
            setLoading(false);
        }
    };

    const generateBBCode = (album: NeteaseAlbum) => {
        const date = new Date(album.publishTime).toLocaleDateString();
        const code = `[img]${album.picUrl}[/img]

[b]Performers:[/b] ${album.artist.name}
[b]Album:[/b] ${album.name}
[b]Released:[/b] ${date}
[b]Company:[/b] ${album.company || 'N/A'}

[b]Uncompressed Cover:[/b] 
[url]${album.picUrl}[/url]

[b]Description:[/b]
${album.description || 'No description available.'}

[b]Tracklist:[/b]
${album.songs.map((s, i) => `${i + 1}. ${s.name} - ${s.artists.map(a => a.name).join(', ')}`).join('\n')}
`;
        setBbcode(code);
    };

    if (pluginId === null) {
        return (
            <div className="p-10 text-center text-gray-500">
                <p>Netease Plugin not found.</p>
                <p className="text-sm mt-2">Please go to Extensions page and install a plugin with "Netease" or "网易" in the name.</p>
            </div>
        );
    }

    return (
        <div className="flex h-full bg-gray-900 text-white overflow-hidden">
            {/* Left Panel: Search */}
            <div className="w-1/3 border-r border-gray-800 flex flex-col">
                <div className="p-4 border-b border-gray-800 bg-gray-900 z-10">
                    <h2 className="text-lg font-bold mb-3 flex items-center gap-2">
                        <Disc className="text-red-500" /> Netease Helper
                    </h2>
                    <div className="flex gap-2">
                        <input
                            type="text"
                            className="flex-1 bg-gray-800 border border-gray-700 rounded px-3 py-2 text-sm focus:outline-none focus:border-red-500"
                            placeholder="Search Song or Album..."
                            value={query}
                            onChange={e => setQuery(e.target.value)}
                            onKeyDown={e => e.key === 'Enter' && search()}
                        />
                        <button
                            onClick={search}
                            disabled={loading}
                            className="bg-red-600 hover:bg-red-500 text-white px-4 py-2 rounded text-sm font-medium transition disabled:brightness-50"
                        >
                            {loading ? <Loader2 className="animate-spin" size={18} /> : <Search size={18} />}
                        </button>
                    </div>
                </div>

                <div className="flex-1 overflow-y-auto p-2 space-y-2">
                    {results.map(song => (
                        <div
                            key={song.id}
                            onClick={() => fetchAlbum(song.album.id)}
                            className={`p-3 rounded-lg cursor-pointer transition flex items-center gap-3 hover:bg-white/10 ${selectedAlbum?.id === song.album.id ? 'bg-white/10 border-l-4 border-red-500' : ''}`}
                        >
                            <img src={song.album.picUrl + "?param=50y50"} alt="" className="w-10 h-10 rounded object-cover" />
                            <div className="overflow-hidden">
                                <div className="font-medium truncate">{song.name}</div>
                                <div className="text-xs text-gray-400 truncate">{song.artists.map(a => a.name).join(', ')} - {song.album.name}</div>
                            </div>
                        </div>
                    ))}
                    {results.length === 0 && !loading && (
                        <div className="text-center text-gray-600 py-10 text-sm">No results</div>
                    )}
                </div>
            </div>

            {/* Right Panel: Album Details & BBCode */}
            <div className="flex-1 flex flex-col bg-black/50">
                {selectedAlbum ? (
                    <div className="flex-1 overflow-y-auto p-6 animate-fade-in">
                        {/* Header Info */}
                        <div className="flex gap-6 mb-8">
                            <div className="w-48 shrink-0">
                                <img src={selectedAlbum.picUrl} className="w-full rounded shadow-2xl" alt="Cover" />
                                <a
                                    href={selectedAlbum.picUrl}
                                    target="_blank"
                                    rel="noreferrer"
                                    className="mt-3 block text-center text-xs text-blue-400 hover:underline flex items-center justify-center gap-1"
                                >
                                    <ExternalLink size={12} /> View Original
                                </a>
                            </div>
                            <div className="flex-1">
                                <h1 className="text-2xl font-bold mb-2">{selectedAlbum.name}</h1>
                                <p className="text-lg text-red-500 font-medium mb-4">{selectedAlbum.artist.name}</p>
                                <div className="grid grid-cols-2 gap-y-2 text-sm text-gray-400 max-w-md">
                                    <span>Company:</span> <span className="text-gray-200">{selectedAlbum.company}</span>
                                    <span>Released:</span> <span className="text-gray-200">{new Date(selectedAlbum.publishTime).toLocaleDateString()}</span>
                                </div>
                            </div>
                        </div>

                        {/* Tools Section */}
                        <div className="mb-6 bg-gray-800 rounded-xl p-4 border border-gray-700">
                            <div className="flex justify-between items-center mb-2">
                                <h3 className="font-bold text-sm text-gray-300">PT Description (BBCode)</h3>
                                <button
                                    onClick={() => navigator.clipboard.writeText(bbcode)}
                                    className="text-xs flex items-center gap-1 bg-white/10 hover:bg-white/20 px-2 py-1 rounded transition"
                                >
                                    <Copy size={12} /> Copy Code
                                </button>
                            </div>
                            <textarea
                                readOnly
                                className="w-full h-40 bg-black/30 text-xs font-mono text-gray-400 p-3 rounded resize-none focus:outline-none"
                                value={bbcode}
                            />
                        </div>

                        {/* Tracklist */}
                        <div>
                            <h3 className="font-bold text-gray-400 mb-3 border-b border-gray-800 pb-2">Tracklist ({selectedAlbum.songs.length})</h3>
                            <div className="space-y-1">
                                {selectedAlbum.songs.map((song, idx) => (
                                    <div key={song.id} className="flex items-center text-sm py-2 hover:bg-white/5 px-2 rounded">
                                        <span className="w-8 text-gray-500 text-right mr-4">{idx + 1}</span>
                                        <span className="flex-1 text-gray-200">{song.name}</span>
                                        <span className="text-gray-500">{Math.floor(song.duration / 60000)}:{String(Math.floor((song.duration % 60000) / 1000)).padStart(2, '0')}</span>
                                    </div>
                                ))}
                            </div>
                        </div>

                    </div>
                ) : (
                    <div className="flex-1 flex flex-col items-center justify-center text-gray-600">
                        <ImageIcon size={48} className="opacity-20 mb-4" />
                        <p>Select an album from search results to generate info.</p>
                    </div>
                )}
            </div>
        </div>
    );
};

export default NeteaseApp;

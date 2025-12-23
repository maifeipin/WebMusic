import { useState, useEffect } from 'react';
import { X, Save, Music2, Tag, Image as ImageIcon, Search, Loader2, Disc } from 'lucide-react';
import api, { updateMedia, getPlugins } from '../services/api';
import CoverPickerModal from './CoverPickerModal';
import SmbImage from './SmbImage';

interface EditSongModalProps {
    isOpen: boolean;
    onClose: () => void;
    song: {
        id: number;
        title: string;
        artist: string;
        album?: string;
        genre?: string;
        coverArt?: string;
    } | null;
    onSaved?: (updatedSong: { id: number; title: string; artist: string; album?: string; genre?: string; coverArt?: string }) => void;
}

interface NeteaseSong {
    id: number;
    name: string;
    artists?: { name: string; img1v1Url?: string; picUrl?: string }[];
    ar?: { name: string; img1v1Url?: string; picUrl?: string }[];
    album?: { id: number; name: string; picUrl?: string };
    al?: { id: number; name: string; picUrl?: string };
    dt: number;
}

export default function EditSongModal({ isOpen, onClose, song, onSaved }: EditSongModalProps) {
    const [title, setTitle] = useState('');
    const [artist, setArtist] = useState('');
    const [album, setAlbum] = useState('');
    const [genre, setGenre] = useState('');
    const [coverArt, setCoverArt] = useState('');

    const [saving, setSaving] = useState(false);
    const [error, setError] = useState('');
    const [isCoverPickerOpen, setIsCoverPickerOpen] = useState(false);

    // Netease State
    const [neteasePluginId, setNeteasePluginId] = useState<number | null>(null);
    const [searchQuery, setSearchQuery] = useState('');
    const [searchResults, setSearchResults] = useState<NeteaseSong[]>([]);
    const [searching, setSearching] = useState(false);

    useEffect(() => {
        if (song) {
            setTitle(song.title || '');
            setArtist(song.artist || '');
            setAlbum(song.album || '');
            setGenre(song.genre || '');
            setCoverArt(song.coverArt || '');
            setError('');
            setSearchResults([]);

            // Init Search Query
            const q = `${song.title} ${song.artist}`.trim();
            setSearchQuery(q);
        }
    }, [song]);

    // Check for plugin
    useEffect(() => {
        if (!isOpen) return;
        const checkPlugin = async () => {
            const plugins = await getPlugins();
            const found = plugins.find(p => p.baseUrl && (p.name.includes("Netease") || p.name.includes("网易")));
            if (found) setNeteasePluginId(found.id);
        };
        checkPlugin();
    }, [isOpen]);

    const handleSearch = async () => {
        if (!neteasePluginId || !searchQuery.trim()) return;
        setSearching(true);
        try {
            const res = await api.get(`/plugins/${neteasePluginId}/proxy/search?keywords=${encodeURIComponent(searchQuery)}`);
            if (res.data?.result?.songs) {
                setSearchResults(res.data.result.songs);
            } else {
                setSearchResults([]);
            }
        } catch (e) {
            console.error(e);
        } finally {
            setSearching(false);
        }
    };

    // Helper to extract the best possible image URL
    const getImageUrl = (song: NeteaseSong) => {
        // 1. Try Album Art (Standard)
        if (song.album?.picUrl) return song.album.picUrl;
        if (song.al?.picUrl) return song.al.picUrl;

        // 2. Try Artist Image (Fallback)
        if (song.artists && song.artists.length > 0 && song.artists[0].img1v1Url) return song.artists[0].img1v1Url;
        if (song.ar && song.ar.length > 0 && song.ar[0].img1v1Url) return song.ar[0].img1v1Url;

        return null;
    };

    const handleAutoFill = (nSong: NeteaseSong) => {
        setTitle(nSong.name);

        const artistName = (nSong.artists || nSong.ar || []).map(a => a.name).join(', ');
        setArtist(artistName);

        const albumObj = nSong.album || nSong.al;
        if (albumObj) {
            setAlbum(albumObj.name);
        }

        const bestPic = getImageUrl(nSong);
        if (bestPic) {
            setCoverArt(bestPic);
        }
    };

    const handleSave = async () => {
        if (!song) return;
        if (!title.trim()) {
            setError('Title is required');
            return;
        }

        setSaving(true);
        setError('');

        try {
            const res = await updateMedia(song.id, {
                title: title.trim(),
                artist: artist.trim(),
                album: album.trim(),
                genre: genre.trim(),
                coverArt: coverArt.trim() || undefined
            });

            onSaved?.({
                id: song.id,
                title: res.data.title,
                artist: res.data.artist,
                album: res.data.album,
                genre: res.data.genre,
                coverArt: res.data.coverArt
            });
            onClose();
        } catch (e) {
            console.error(e);
            setError('Failed to save changes');
        } finally {
            setSaving(false);
        }
    };

    if (!isOpen || !song) return null;

    const hasPlugin = neteasePluginId !== null;

    return (
        <>
            <div className="fixed inset-0 z-[80] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
                <div className={`bg-gray-900 border border-gray-800 rounded-2xl w-full shadow-2xl animate-fade-in flex flex-col max-h-[90vh] ${hasPlugin ? 'max-w-5xl md:w-[90vw]' : 'max-w-md'}`}>

                    {/* Header */}
                    <div className="flex items-center justify-between p-4 border-b border-gray-800 flex-shrink-0">
                        <div className="flex items-center gap-2">
                            <Music2 size={20} className="text-blue-500" />
                            <h2 className="text-lg font-bold">Edit Metadata</h2>
                        </div>
                        <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition">
                            <X size={20} />
                        </button>
                    </div>

                    {/* Body Grid */}
                    <div className={`flex flex-1 overflow-hidden ${hasPlugin ? 'flex-col md:flex-row' : ''}`}>

                        {/* LEFT: Form */}
                        <div className={`p-6 overflow-y-auto custom-scrollbar space-y-6 ${hasPlugin ? 'md:w-1/2 border-r border-gray-800' : 'w-full'}`}>
                            {error && (
                                <div className="p-3 bg-red-500/20 border border-red-500/50 rounded-lg text-red-400 text-sm">
                                    {error}
                                </div>
                            )}

                            {/* Cover Art Section */}
                            <div className="flex items-center gap-5 p-4 bg-gray-800/30 rounded-xl border border-gray-700/50">
                                <div className="w-24 h-24 bg-gray-900 rounded-lg flex items-center justify-center overflow-hidden border border-gray-700 flex-shrink-0 relative group shadow-inner">
                                    {coverArt ? (
                                        <SmbImage
                                            smbPath={coverArt}
                                            alt="Cover"
                                            className="w-full h-full object-cover"
                                            fallbackClassName="text-gray-600"
                                        />
                                    ) : (
                                        <ImageIcon size={32} className="text-gray-700" />
                                    )}
                                    <div className="absolute inset-0 bg-black/50 opacity-0 group-hover:opacity-100 transition flex items-center justify-center pointer-events-none">
                                        <span className="text-xs text-white font-medium bg-black/50 px-2 py-1 rounded">Change</span>
                                    </div>
                                </div>
                                <div className="flex-1 min-w-0 space-y-2">
                                    <div>
                                        <label className="block text-xs text-gray-400 uppercase tracking-wider font-bold mb-0.5">Cover Art Source</label>
                                        <input
                                            value={coverArt}
                                            onChange={(e) => setCoverArt(e.target.value)}
                                            className="w-full bg-black/30 border border-gray-800 rounded px-2 py-1 text-xs text-gray-300 font-mono focus:outline-none focus:border-blue-500 transition"
                                            placeholder="https://... or /path/to/file.jpg"
                                        />
                                    </div>
                                    <button
                                        onClick={() => setIsCoverPickerOpen(true)}
                                        className="px-3 py-1.5 text-xs bg-gray-800 hover:bg-gray-700 hover:text-white text-gray-300 rounded-lg transition flex items-center gap-2 border border-gray-700"
                                    >
                                        <ImageIcon size={14} />
                                        Browse Local Files...
                                    </button>
                                </div>
                            </div>

                            <div className="space-y-4">
                                <div>
                                    <label className="block text-sm font-medium text-gray-400 mb-1.5">Title</label>
                                    <input
                                        type="text"
                                        value={title}
                                        onChange={(e) => setTitle(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500/50 transition text-white placeholder-gray-600"
                                        placeholder="Song Title"
                                    />
                                </div>
                                <div>
                                    <label className="block text-sm font-medium text-gray-400 mb-1.5">Artist</label>
                                    <input
                                        type="text"
                                        value={artist}
                                        onChange={(e) => setArtist(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500/50 transition text-white placeholder-gray-600"
                                        placeholder="Artist Name"
                                    />
                                </div>
                                <div>
                                    <label className="block text-sm font-medium text-gray-400 mb-1.5">Album</label>
                                    <input
                                        type="text"
                                        value={album}
                                        onChange={(e) => setAlbum(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500/50 transition text-white placeholder-gray-600"
                                        placeholder="Album Name"
                                    />
                                </div>
                                <div>
                                    <label className="block text-sm font-medium text-gray-400 mb-1.5">
                                        <span className="flex items-center gap-1.5">
                                            <Tag size={14} /> Genre
                                        </span>
                                    </label>
                                    <input
                                        type="text"
                                        value={genre}
                                        onChange={(e) => setGenre(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500/50 transition text-white placeholder-gray-600"
                                        placeholder="Genre"
                                    />
                                </div>
                            </div>
                        </div>

                        {/* RIGHT: Netease Match (Only if plugin exists) */}
                        {hasPlugin && (
                            <div className="flex flex-col bg-gray-900/50 md:w-1/2">
                                <div className="p-4 border-b border-gray-800 bg-gray-900">
                                    <div className="bg-red-500/10 text-red-400 text-xs font-bold px-2 py-1 rounded w-fit mb-3 flex items-center gap-1.5 border border-red-500/20">
                                        <Disc size={12} />
                                        NETEASE CLOUD MATCH
                                    </div>
                                    <div className="flex gap-2">
                                        <input
                                            value={searchQuery}
                                            onChange={e => setSearchQuery(e.target.value)}
                                            onKeyDown={e => e.key === 'Enter' && handleSearch()}
                                            className="flex-1 bg-black/50 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-red-500 text-white placeholder-gray-600"
                                            placeholder="Search metadata..."
                                        />
                                        <button
                                            onClick={handleSearch}
                                            disabled={searching}
                                            className="bg-red-600 hover:bg-red-500 text-white px-4 py-2 rounded-lg text-sm transition font-medium disabled:opacity-50 disabled:cursor-not-allowed"
                                        >
                                            {searching ? <Loader2 size={18} className="animate-spin" /> : <Search size={18} />}
                                        </button>
                                    </div>
                                </div>

                                <div className="flex-1 overflow-y-auto p-2 space-y-1 custom-scrollbar">
                                    {searchResults.map(res => {
                                        const picUrl = getImageUrl(res);
                                        return (
                                            <div key={res.id} className="group p-2 rounded-lg hover:bg-gray-800 transition flex gap-3 items-center border border-transparent hover:border-gray-700">
                                                <div className="w-12 h-12 flex-shrink-0 bg-gray-800 rounded overflow-hidden">
                                                    {picUrl ? (
                                                        <img
                                                            src={picUrl + "?param=100y100"}
                                                            className="w-full h-full object-cover"
                                                            referrerPolicy="no-referrer"
                                                            alt=""
                                                            onError={(e) => {
                                                                (e.target as HTMLImageElement).style.display = 'none';
                                                            }}
                                                        />
                                                    ) : (
                                                        <div className="w-full h-full flex items-center justify-center text-gray-600">
                                                            <Disc size={20} />
                                                        </div>
                                                    )}
                                                </div>
                                                <div className="flex-1 overflow-hidden min-w-0">
                                                    <div className="font-bold text-sm truncate text-gray-200" title={res.name}>{res.name}</div>
                                                    <div className="text-xs text-gray-400 truncate" title={`${(res.artists || res.ar || []).map(a => a.name).join(', ')} - ${res.album?.name || res.al?.name}`}>
                                                        <span className="text-gray-300">{(res.artists || res.ar || []).map(a => a.name).join(', ')}</span>
                                                        <span className="text-gray-600 mx-1">•</span>
                                                        <span>{res.album?.name || res.al?.name}</span>
                                                    </div>
                                                </div>
                                                <button
                                                    onClick={() => handleAutoFill(res)}
                                                    className="px-3 py-1.5 bg-blue-600/10 text-blue-400 hover:bg-blue-600 hover:text-white border border-blue-600/20 hover:border-blue-600 rounded text-xs font-medium transition whitespace-nowrap"
                                                >
                                                    Auto Fill
                                                </button>
                                            </div>
                                        );
                                    })}
                                    {searchResults.length === 0 && !searching && (
                                        <div className="text-center text-gray-500 py-12 flex flex-col items-center gap-2">
                                            <Search size={32} className="opacity-20" />
                                            <span className="text-xs">Search for a song to match metadata</span>
                                        </div>
                                    )}
                                </div>
                            </div>
                        )}
                    </div>

                    {/* Footer */}
                    <div className="p-4 border-t border-gray-800 flex justify-end gap-3 flex-shrink-0 bg-gray-900 rounded-b-2xl">
                        <button
                            onClick={onClose}
                            className="px-4 py-2 text-gray-400 hover:text-white hover:bg-white/5 rounded-lg transition"
                        >
                            Cancel
                        </button>
                        <button
                            onClick={handleSave}
                            disabled={saving || !title.trim()}
                            className="px-4 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-lg flex items-center gap-2 transition disabled:opacity-50 disabled:cursor-not-allowed shadow-lg shadow-blue-900/20"
                        >
                            <Save size={18} />
                            {saving ? 'Saving...' : 'Save Changes'}
                        </button>
                    </div>
                </div>
            </div>

            <CoverPickerModal
                isOpen={isCoverPickerOpen}
                onClose={() => setIsCoverPickerOpen(false)}
                onSelect={(path) => setCoverArt(path)}
                currentCover={coverArt}
            />
        </>
    );
}

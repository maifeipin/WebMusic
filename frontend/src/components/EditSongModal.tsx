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
    artists?: { name: string }[];
    ar?: { name: string }[];
    album?: { id: number; name: string; picUrl: string };
    al?: { id: number; name: string; picUrl: string };
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

    const handleAutoFill = (nSong: NeteaseSong) => {
        setTitle(nSong.name);

        const artistName = (nSong.artists || nSong.ar || []).map(a => a.name).join(', ');
        setArtist(artistName);

        const albumObj = nSong.album || nSong.al;
        if (albumObj) {
            setAlbum(albumObj.name);
            if (albumObj.picUrl) {
                setCoverArt(albumObj.picUrl);
            }
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
                <div className={`bg-gray-900 border border-gray-800 rounded-2xl w-full shadow-2xl animate-fade-in flex flex-col max-h-[90vh] ${hasPlugin ? 'max-w-4xl' : 'max-w-md'}`}>

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
                        <div className="flex-1 p-4 overflow-y-auto custom-scrollbar space-y-4">
                            {error && (
                                <div className="p-3 bg-red-500/20 border border-red-500/50 rounded-lg text-red-400 text-sm">
                                    {error}
                                </div>
                            )}

                            {/* Cover Art Section */}
                            <div className="flex items-center gap-4 p-3 bg-gray-800/50 rounded-xl border border-gray-700/50">
                                <div className="w-20 h-20 bg-gray-800 rounded-lg flex items-center justify-center overflow-hidden border border-gray-700 flex-shrink-0 relative group">
                                    {coverArt ? (
                                        <SmbImage
                                            smbPath={coverArt}
                                            alt="Cover"
                                            className="w-full h-full object-cover"
                                            fallbackClassName="text-gray-600"
                                        />
                                    ) : (
                                        <ImageIcon size={24} className="text-gray-600" />
                                    )}
                                    <div className="absolute inset-0 bg-black/50 opacity-0 group-hover:opacity-100 transition flex items-center justify-center pointer-events-none">
                                        <span className="text-xs text-white">Change</span>
                                    </div>
                                </div>
                                <div className="flex-1 min-w-0">
                                    <label className="block text-xs text-gray-400 mb-1 uppercase tracking-wider font-semibold">Cover Art</label>
                                    <div className="text-xs text-gray-300 truncate mb-2" title={coverArt}>
                                        {coverArt ? (coverArt.startsWith('http') ? 'Online Image' : coverArt) : 'No cover set'}
                                    </div>
                                    <button
                                        onClick={() => setIsCoverPickerOpen(true)}
                                        className="px-3 py-1.5 text-xs bg-gray-700 hover:bg-gray-600 text-white rounded-lg transition flex items-center gap-1.5 w-fit"
                                    >
                                        <ImageIcon size={14} />
                                        Local File...
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
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                    />
                                </div>
                                <div>
                                    <label className="block text-sm font-medium text-gray-400 mb-1.5">Artist</label>
                                    <input
                                        type="text"
                                        value={artist}
                                        onChange={(e) => setArtist(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                    />
                                </div>
                                <div>
                                    <label className="block text-sm font-medium text-gray-400 mb-1.5">Album</label>
                                    <input
                                        type="text"
                                        value={album}
                                        onChange={(e) => setAlbum(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
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
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                    />
                                </div>
                            </div>
                        </div>

                        {/* RIGHT: Netease Match (Only if plugin exists) */}
                        {hasPlugin && (
                            <div className="flex-1 border-t md:border-t-0 md:border-l border-gray-800 flex flex-col bg-black/20">
                                <div className="p-3 border-b border-gray-800 bg-gray-900/50">
                                    <div className="bg-red-900/20 text-red-500 text-xs font-bold px-2 py-1 rounded w-fit mb-2 flex items-center gap-1">
                                        <Disc size={12} /> NETEASE MATCH
                                    </div>
                                    <div className="flex gap-2">
                                        <input
                                            value={searchQuery}
                                            onChange={e => setSearchQuery(e.target.value)}
                                            onKeyDown={e => e.key === 'Enter' && handleSearch()}
                                            className="flex-1 bg-black/50 border border-gray-700 rounded px-3 py-1.5 text-sm focus:outline-none focus:border-red-500"
                                            placeholder="Search metadata..."
                                        />
                                        <button
                                            onClick={handleSearch}
                                            disabled={searching}
                                            className="bg-red-600 hover:bg-red-500 text-white px-3 py-1.5 rounded text-sm transition"
                                        >
                                            {searching ? <Loader2 size={16} className="animate-spin" /> : <Search size={16} />}
                                        </button>
                                    </div>
                                </div>

                                <div className="flex-1 overflow-y-auto p-3 space-y-2">
                                    {searchResults.map(res => (
                                        <div key={res.id} className="group p-2 rounded bg-gray-800/30 hover:bg-gray-700 transition flex gap-3 items-center">
                                            <img
                                                src={(res.album?.picUrl || res.al?.picUrl) + "?param=50y50"}
                                                className="w-10 h-10 rounded object-cover bg-gray-900"
                                                referrerPolicy="no-referrer"
                                                alt=""
                                            />
                                            <div className="flex-1 overflow-hidden">
                                                <div className="font-bold text-sm truncate text-gray-200">{res.name}</div>
                                                <div className="text-xs text-gray-400 truncate">
                                                    {(res.artists || res.ar || []).map(a => a.name).join(', ')} - {res.album?.name || res.al?.name}
                                                </div>
                                            </div>
                                            <button
                                                onClick={() => handleAutoFill(res)}
                                                className="px-3 py-1.5 bg-blue-600/20 text-blue-400 hover:bg-blue-600 hover:text-white rounded text-xs font-medium opacity-0 group-hover:opacity-100 transition whitespace-nowrap"
                                            >
                                                Auto Fill
                                            </button>
                                        </div>
                                    ))}
                                    {searchResults.length === 0 && !searching && (
                                        <div className="text-center text-gray-600 py-10 text-xs">
                                            No matches found.
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

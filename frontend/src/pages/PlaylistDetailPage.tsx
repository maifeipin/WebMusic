import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getPlaylist, removeSongsFromPlaylist } from '../services/api';
import { usePlayer } from '../context/PlayerContext';
import { Play, ArrowLeft, Trash2, Clock, CheckSquare, Square } from 'lucide-react';
import { formatRelativeTime } from '../utils/time';

interface PlaylistDetail {
    id: number;
    name: string;
    createdAt: string;
    songs: {
        id: number; // PlaylistSong ID
        song: {
            id: number; // MediaFile ID
            title: string;
            artist: string;
            album: string;
            duration: string;
        };
        addedAt: string;
    }[];
}

export default function PlaylistDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const { playQueue } = usePlayer();

    const [playlist, setPlaylist] = useState<PlaylistDetail | null>(null);
    const [loading, setLoading] = useState(true);
    const [selectedItems, setSelectedItems] = useState<number[]>([]); // These are MediaFile IDs to align with remove API

    useEffect(() => {
        if (id) loadData();
    }, [id]);

    const loadData = async () => {
        try {
            setLoading(true);
            const res = await getPlaylist(Number(id));
            setPlaylist(res.data);
            setSelectedItems([]);
        } catch (error) {
            console.error(error);
            navigate('/playlists');
        } finally {
            setLoading(false);
        }
    };

    const handlePlayAll = () => {
        if (!playlist || playlist.songs.length === 0) return;
        // Transform to QueueItem
        const queue = playlist.songs.map(item => ({
            id: item.song.id,
            title: item.song.title,
            artist: item.song.artist,
            album: item.song.album,
            duration: 0, // Default to 0 number to satisfy TS
        })) as any; // Cast to avoid strict check on Song property mismatch
        playQueue(queue, 0);
    };

    const toggleSelect = (mediaFileId: number) => {
        setSelectedItems(prev =>
            prev.includes(mediaFileId) ? prev.filter(x => x !== mediaFileId) : [...prev, mediaFileId]
        );
    };

    const handleDeleteSelected = async () => {
        if (selectedItems.length === 0) return;
        if (!confirm(`Remove ${selectedItems.length} songs from playlist?`)) return;

        try {
            await removeSongsFromPlaylist(Number(id), selectedItems);
            loadData();
        } catch (e) {
            alert("Failed to remove songs");
        }
    };

    if (loading) return <div className="p-8 text-gray-500">Loading...</div>;
    if (!playlist) return null;

    return (
        <div className="p-6 md:p-8 min-h-screen pb-32">
            {/* Header */}
            <div className="flex items-center gap-4 mb-8">
                <button onClick={() => navigate('/playlists')} className="p-2 hover:bg-white/10 rounded-full text-gray-400 hover:text-white transition">
                    <ArrowLeft size={24} />
                </button>
                <div className="flex-1">
                    <h1 className="text-3xl font-bold tracking-tight">{playlist.name}</h1>
                    <div className="flex items-center gap-2 text-sm text-gray-500 mt-1">
                        <span>{playlist.songs.length} songs</span>
                        <span>•</span>
                        <span>Created {new Date(playlist.createdAt).toLocaleDateString()}</span>
                    </div>
                </div>
                <div className="flex gap-3">
                    {selectedItems.length > 0 && (
                        <button
                            onClick={handleDeleteSelected}
                            className="px-4 py-2 bg-red-500/10 text-red-500 hover:bg-red-500/20 rounded-lg flex items-center gap-2 font-medium transition"
                        >
                            <Trash2 size={18} />
                            Remove Selected ({selectedItems.length})
                        </button>
                    )}
                    <button
                        onClick={handlePlayAll}
                        className="px-6 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-lg flex items-center gap-2 font-medium transition shadow-lg shadow-blue-900/20"
                    >
                        <Play size={20} fill="currentColor" />
                        Play All
                    </button>
                </div>
            </div>

            {/* List */}
            <div className="bg-gray-900/50 border border-gray-800/50 rounded-2xl overflow-hidden">
                <div className="grid grid-cols-[auto_40px_1fr_1fr_100px] gap-4 p-4 border-b border-gray-800 text-sm font-medium text-gray-400 select-none">
                    <div className="w-6"></div> {/* Checkbox */}
                    <div>#</div>
                    <div>Title</div>
                    <div>Artist</div>
                    <div className="text-right">Added</div>
                </div>

                {playlist.songs.length === 0 && (
                    <div className="p-8 text-center text-gray-500">
                        This playlist is empty.
                    </div>
                )}

                {playlist.songs.map((item, i) => {
                    const isSelected = selectedItems.includes(item.song.id);
                    return (
                        <div
                            key={item.id}
                            onClick={() => toggleSelect(item.song.id)}
                            className={`grid grid-cols-[auto_40px_1fr_1fr_100px] gap-4 p-4 items-center hover:bg-white/5 transition cursor-pointer group ${isSelected ? 'bg-blue-500/10' : ''}`}
                        >
                            <div className="w-6 text-gray-500 group-hover:text-blue-500">
                                {isSelected ? <CheckSquare size={18} /> : <Square size={18} />}
                            </div>
                            <div className="text-gray-500 font-mono text-sm">{i + 1}</div>
                            <div className="font-medium text-gray-200 truncate">{item.song.title}</div>
                            <div className="text-gray-400 truncate">{item.song.artist}</div>
                            <div className="text-right text-gray-500 text-sm">{formatRelativeTime(item.addedAt)}</div>
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

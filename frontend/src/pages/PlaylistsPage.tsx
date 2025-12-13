import { useState, useEffect } from 'react';
import { Plus, Play, Music, Trash2, Share2, Link, Copy } from 'lucide-react';
import { getPlaylists, deletePlaylist, getPlaylist, revokePlaylistShare } from '../services/api';
import CreatePlaylistModal from '../components/CreatePlaylistModal';
import SmbImage from '../components/SmbImage';
import { useNavigate } from 'react-router-dom';
import { usePlayer } from '../context/PlayerContext';

interface Playlist {
    id: number;
    name: string;
    coverArt?: string;
    count: number;
    type: string;
    shareToken?: string;
}

export default function PlaylistsPage() {
    const [normalPlaylists, setNormalPlaylists] = useState<Playlist[]>([]);
    const [sharedPlaylists, setSharedPlaylists] = useState<Playlist[]>([]);
    const [loading, setLoading] = useState(true);
    const [isCreateOpen, setIsCreateOpen] = useState(false);
    const [activeTab, setActiveTab] = useState<'normal' | 'shared'>('normal');

    const navigate = useNavigate();
    const { playQueue } = usePlayer();

    useEffect(() => {
        loadData();
    }, []);

    const loadData = async () => {
        try {
            setLoading(true);
            const [normalRes, sharedRes] = await Promise.all([
                getPlaylists('normal'),
                getPlaylists('shared')
            ]);
            setNormalPlaylists(normalRes.data);
            setSharedPlaylists(sharedRes.data);
        } catch (error) {
            console.error(error);
        } finally {
            setLoading(false);
        }
    };

    const handleDelete = async (e: React.MouseEvent, id: number) => {
        e.stopPropagation();
        if (!confirm('Are you sure you want to delete this playlist?')) return;
        try {
            await deletePlaylist(id);
            loadData();
        } catch (e) {
            alert('Failed to delete');
        }
    };

    const handlePlayAll = async (e: React.MouseEvent, id: number) => {
        e.stopPropagation();
        try {
            const res = await getPlaylist(id);
            const songs = res.data.songs.map((s: any) => ({
                id: s.song.id,
                title: s.song.title,
                artist: s.song.artist,
                album: s.song.album,
                duration: 0,
                filePath: s.song.filePath
            }));
            playQueue(songs, 0);
        } catch (e) {
            console.error(e);
        }
    };

    const handleCopyShareLink = async (e: React.MouseEvent, token: string) => {
        e.stopPropagation();
        const url = `${window.location.origin}/share/${token}`;
        try {
            await navigator.clipboard.writeText(url);
            alert('✅ Share link copied to clipboard!');
        } catch {
            prompt('Copy this link:', url);
        }
    };

    const handleRevokeShare = async (e: React.MouseEvent, id: number) => {
        e.stopPropagation();
        if (!confirm('Revoke share link?')) return;
        try {
            await revokePlaylistShare(id);
            loadData();
        } catch (e) {
            alert('Failed to revoke');
        }
    };

    const currentPlaylists = activeTab === 'normal' ? normalPlaylists : sharedPlaylists;

    return (
        <div className="p-6 md:p-8 space-y-6 min-h-screen pb-32">
            <div className="flex items-center justify-between">
                <div>
                    <h1 className="text-3xl font-bold tracking-tight mb-2">Playlists</h1>
                    <p className="text-gray-400">Manage your custom collections.</p>
                </div>
                <button
                    onClick={() => setIsCreateOpen(true)}
                    className="flex items-center gap-2 bg-blue-600 hover:bg-blue-500 text-white px-4 py-2 rounded-lg transition font-medium shadow-lg shadow-blue-900/20"
                >
                    <Plus size={20} />
                    <span>Create New</span>
                </button>
            </div>

            {/* Tabs */}
            <div className="flex gap-1 bg-gray-900/50 p-1 rounded-xl w-fit">
                <button
                    onClick={() => setActiveTab('normal')}
                    className={`px-4 py-2 rounded-lg font-medium transition ${activeTab === 'normal'
                        ? 'bg-blue-600 text-white'
                        : 'text-gray-400 hover:text-white hover:bg-white/5'
                        }`}
                >
                    My Playlists ({normalPlaylists.length})
                </button>
                <button
                    onClick={() => setActiveTab('shared')}
                    className={`px-4 py-2 rounded-lg font-medium transition flex items-center gap-2 ${activeTab === 'shared'
                        ? 'bg-emerald-600 text-white'
                        : 'text-gray-400 hover:text-white hover:bg-white/5'
                        }`}
                >
                    <Share2 size={16} />
                    Shared ({sharedPlaylists.length})
                </button>
            </div>

            {loading ? (
                <div className="text-gray-500">Loading...</div>
            ) : (
                <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-6">
                    {currentPlaylists.length === 0 && (
                        <div
                            onClick={() => activeTab === 'normal' && setIsCreateOpen(true)}
                            className={`aspect-square rounded-2xl border-2 border-dashed border-gray-800 ${activeTab === 'normal' ? 'hover:border-blue-500/50 hover:bg-blue-500/5 cursor-pointer' : ''
                                } flex flex-col items-center justify-center transition group`}
                        >
                            <div className="w-16 h-16 rounded-full bg-gray-900 flex items-center justify-center mb-4 text-gray-500">
                                {activeTab === 'normal' ? <Plus size={32} /> : <Share2 size={32} />}
                            </div>
                            <span className="font-medium text-gray-400">
                                {activeTab === 'normal' ? 'Create Playlist' : 'No shared playlists'}
                            </span>
                        </div>
                    )}

                    {currentPlaylists.map(p => (
                        <div
                            key={p.id}
                            onClick={() => navigate(`/playlists/${p.id}`)}
                            className="bg-gray-900/50 border border-gray-800/50 rounded-2xl p-4 hover:bg-gray-800 transition cursor-pointer group relative overflow-hidden"
                        >
                            {/* Share Badge */}
                            {p.shareToken && (
                                <div className="absolute top-2 right-2 z-10 px-2 py-1 bg-emerald-500/20 text-emerald-400 text-xs rounded-full flex items-center gap-1">
                                    <Link size={10} />
                                    Shared
                                </div>
                            )}

                            <div className="aspect-square bg-gradient-to-br from-gray-800 to-gray-900 rounded-xl mb-4 flex items-center justify-center relative shadow-lg overflow-hidden">
                                {p.coverArt ? (
                                    <SmbImage
                                        smbPath={p.coverArt}
                                        alt={p.name}
                                        className="w-full h-full object-cover"
                                        fallbackClassName="text-gray-700"
                                    />
                                ) : (
                                    <Music size={48} className="text-gray-700" />
                                )}

                                <div className="absolute inset-0 bg-black/40 opacity-0 group-hover:opacity-100 transition flex items-center justify-center backdrop-blur-[2px]">
                                    <button
                                        onClick={(e) => handlePlayAll(e, p.id)}
                                        className="w-14 h-14 bg-blue-600 text-white rounded-full flex items-center justify-center hover:scale-105 transition shadow-xl"
                                    >
                                        <Play size={24} fill="currentColor" className="ml-1" />
                                    </button>
                                </div>
                            </div>

                            <div className="flex items-start justify-between">
                                <div className="flex-1 min-w-0">
                                    <h3 className="font-bold text-lg truncate pr-2 group-hover:text-blue-400 transition">{p.name}</h3>
                                    <p className="text-sm text-gray-500">{p.count} songs</p>
                                </div>
                                <div className="opacity-0 group-hover:opacity-100 transition flex gap-1">
                                    {p.shareToken && (
                                        <button
                                            onClick={(e) => handleCopyShareLink(e, p.shareToken!)}
                                            className="p-1.5 text-gray-500 hover:text-emerald-400 hover:bg-white/5 rounded-lg"
                                            title="Copy share link"
                                        >
                                            <Copy size={16} />
                                        </button>
                                    )}
                                    {activeTab === 'shared' ? (
                                        <button
                                            onClick={(e) => handleRevokeShare(e, p.id)}
                                            className="p-1.5 text-gray-500 hover:text-orange-400 hover:bg-white/5 rounded-lg"
                                            title="Revoke share"
                                        >
                                            <Share2 size={16} />
                                        </button>
                                    ) : null}
                                    <button
                                        onClick={(e) => handleDelete(e, p.id)}
                                        className="p-1.5 text-gray-500 hover:text-red-400 hover:bg-white/5 rounded-lg"
                                        title="Delete"
                                    >
                                        <Trash2 size={16} />
                                    </button>
                                </div>
                            </div>
                        </div>
                    ))}
                </div>
            )}

            <CreatePlaylistModal
                isOpen={isCreateOpen}
                onClose={() => setIsCreateOpen(false)}
                onSuccess={loadData}
            />
        </div>
    );
}

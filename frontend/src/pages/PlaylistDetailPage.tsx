import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getPlaylist, removeSongsFromPlaylist, updatePlaylist, sharePlaylist } from '../services/api';
import { usePlayer } from '../context/PlayerContext';
import { Play, ArrowLeft, Trash2, CheckSquare, Square, Edit2, Save, X, Image as ImageIcon, Share2, Copy, ExternalLink, Lock, Clock } from 'lucide-react';
import { formatRelativeTime } from '../utils/time';
import CoverPickerModal from '../components/CoverPickerModal';
import SmbImage from '../components/SmbImage';

interface PlaylistDetail {
    id: number;
    name: string;
    coverArt?: string;
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

    // Edit mode state
    const [isEditing, setIsEditing] = useState(false);
    const [editName, setEditName] = useState('');
    const [editCoverArt, setEditCoverArt] = useState('');
    const [saving, setSaving] = useState(false);
    const [isCoverPickerOpen, setIsCoverPickerOpen] = useState(false);

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

    const handleStartEdit = () => {
        if (!playlist) return;
        setEditName(playlist.name);
        setEditCoverArt(playlist.coverArt || '');
        setIsEditing(true);
    };

    const handleCancelEdit = () => {
        setIsEditing(false);
    };

    const handleSaveEdit = async () => {
        if (!playlist || !editName.trim()) return;
        setSaving(true);
        try {
            await updatePlaylist(playlist.id, {
                name: editName.trim(),
                coverArt: editCoverArt.trim() || undefined
            });
            await loadData();
            setIsEditing(false);
        } catch (e) {
            alert('Failed to update playlist');
        } finally {
            setSaving(false);
        }
    };

    const handlePlayAll = () => {
        if (!playlist || playlist.songs.length === 0) return;

        // If songs are selected, play only selected; otherwise play all
        let songsToPlay = playlist.songs;
        if (selectedItems.length > 0) {
            songsToPlay = playlist.songs.filter(item => selectedItems.includes(item.song.id));
        }

        if (songsToPlay.length === 0) return;

        // Transform to QueueItem
        const queue = songsToPlay.map(item => ({
            id: item.song.id,
            title: item.song.title,
            artist: item.song.artist,
            album: item.song.album,
            duration: 0,
        })) as any;

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

    // Share state
    const [showShareModal, setShowShareModal] = useState(false);
    const [shareName, setShareName] = useState('');
    const [shareExpireDays, setShareExpireDays] = useState(7); // Default 7 days
    const [sharePassword, setSharePassword] = useState('');
    const [shareResult, setShareResult] = useState<{ token: string; url: string } | null>(null);
    const [sharing, setSharing] = useState(false);

    const handleOpenShareModal = () => {
        if (!playlist) return;
        setShareName(selectedItems.length > 0 ? `${playlist.name} - Selection` : playlist.name);
        // Reset defaults
        setShareExpireDays(7);
        setSharePassword('');
        setShareResult(null);
        setShowShareModal(true);
    };

    const handleShare = async () => {
        if (!playlist) return;
        setSharing(true);
        try {
            const songIds = selectedItems.length > 0 ? selectedItems : undefined;
            const res = await sharePlaylist(playlist.id, {
                name: shareName.trim(),
                songIds,
                expiresInDays: shareExpireDays,
                password: sharePassword.trim() || undefined
            });
            setShareResult({ token: res.data.shareToken, url: `${window.location.origin}/share/${res.data.shareToken}` });
        } catch (e) {
            alert('Failed to generate share link');
        } finally {
            setSharing(false);
        }
    };

    const handleCopyShareLink = async () => {
        if (!shareResult) return;
        try {
            await navigator.clipboard.writeText(shareResult.url);
            alert('✅ Link copied to clipboard!');
        } catch (err) {
            // Fallback for browsers that don't support clipboard API
            prompt('Copy this link:', shareResult.url);
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

                {isEditing ? (
                    <div className="flex-1 space-y-3">
                        <input
                            type="text"
                            value={editName}
                            onChange={(e) => setEditName(e.target.value)}
                            className="bg-black/50 border border-gray-700 rounded-lg px-4 py-2 text-2xl font-bold w-full focus:outline-none focus:border-blue-500 transition"
                            placeholder="Playlist name"
                        />
                        <div className="flex items-center gap-3">
                            {/* Cover Preview */}
                            <div className="w-16 h-16 bg-gray-800 rounded-lg flex items-center justify-center overflow-hidden border border-gray-700 flex-shrink-0">
                                {editCoverArt ? (
                                    <SmbImage
                                        smbPath={editCoverArt}
                                        alt="Cover"
                                        className="w-full h-full object-cover"
                                        fallbackClassName="text-gray-600"
                                    />
                                ) : (
                                    <ImageIcon size={24} className="text-gray-600" />
                                )}
                            </div>
                            <div className="flex-1 flex flex-col gap-1">
                                <div className="text-xs text-gray-400 truncate max-w-md" title={editCoverArt}>
                                    {editCoverArt || 'No cover selected'}
                                </div>
                                <button
                                    type="button"
                                    onClick={() => setIsCoverPickerOpen(true)}
                                    className="self-start px-3 py-1.5 text-xs bg-gray-800 hover:bg-gray-700 text-gray-300 rounded-lg transition flex items-center gap-1"
                                >
                                    <ImageIcon size={14} />
                                    Browse SMB...
                                </button>
                            </div>
                        </div>
                        <div className="flex items-center gap-2 text-sm text-gray-500">
                            <span>{playlist.songs.length} songs</span>
                            <span>•</span>
                            <span>Created {new Date(playlist.createdAt).toLocaleDateString()}</span>
                        </div>
                    </div>
                ) : (
                    <div className="flex-1">
                        <div className="flex items-center gap-2">
                            <h1 className="text-3xl font-bold tracking-tight">{playlist.name}</h1>
                            <button
                                onClick={handleStartEdit}
                                className="p-1.5 text-gray-500 hover:text-white hover:bg-white/10 rounded-lg transition"
                                title="Edit playlist"
                            >
                                <Edit2 size={16} />
                            </button>
                        </div>
                        {playlist.coverArt && (
                            <div className="text-xs text-gray-600 mt-1 truncate max-w-md" title={playlist.coverArt}>
                                Cover: {playlist.coverArt}
                            </div>
                        )}
                        <div className="flex items-center gap-2 text-sm text-gray-500 mt-1">
                            <span>{playlist.songs.length} songs</span>
                            <span>•</span>
                            <span>Created {new Date(playlist.createdAt).toLocaleDateString()}</span>
                        </div>
                    </div>
                )}

                <div className="flex gap-3">
                    {isEditing ? (
                        <>
                            <button
                                onClick={handleCancelEdit}
                                className="px-4 py-2 text-gray-400 hover:text-white hover:bg-white/5 rounded-lg flex items-center gap-2 font-medium transition"
                            >
                                <X size={18} />
                                Cancel
                            </button>
                            <button
                                onClick={handleSaveEdit}
                                disabled={saving || !editName.trim()}
                                className="px-4 py-2 bg-green-600 hover:bg-green-500 text-white rounded-lg flex items-center gap-2 font-medium transition disabled:opacity-50"
                            >
                                <Save size={18} />
                                {saving ? 'Saving...' : 'Save'}
                            </button>
                        </>
                    ) : (
                        <>
                            {selectedItems.length > 0 && (
                                <button
                                    onClick={handleDeleteSelected}
                                    className="px-4 py-2 bg-red-500/10 text-red-500 hover:bg-red-500/20 rounded-lg flex items-center gap-2 font-medium transition"
                                >
                                    <Trash2 size={18} />
                                    Remove ({selectedItems.length})
                                </button>
                            )}
                            <button
                                onClick={handleOpenShareModal}
                                className="px-4 py-2 bg-emerald-600/20 text-emerald-400 hover:bg-emerald-600/30 rounded-lg flex items-center gap-2 font-medium transition"
                            >
                                <Share2 size={18} />
                                {selectedItems.length > 0 ? `Share (${selectedItems.length})` : 'Share All'}
                            </button>
                            <button
                                onClick={handlePlayAll}
                                className="px-6 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-lg flex items-center gap-2 font-medium transition shadow-lg shadow-blue-900/20"
                            >
                                <Play size={20} fill="currentColor" />
                                {selectedItems.length > 0 ? `Play (${selectedItems.length})` : 'Play All'}
                            </button>
                        </>
                    )}
                </div>
            </div>

            {/* List */}
            <div className="bg-gray-900/50 border border-gray-800/50 rounded-2xl overflow-hidden">
                <div className="grid grid-cols-[auto_40px_1fr_1fr_100px] gap-4 p-4 border-b border-gray-800 text-sm font-medium text-gray-400 select-none">
                    {/* Select All / None Checkbox */}
                    <div
                        className="w-6 cursor-pointer hover:text-blue-400 transition"
                        onClick={() => {
                            if (!playlist) return;
                            if (selectedItems.length === playlist.songs.length) {
                                // All selected -> clear all
                                setSelectedItems([]);
                            } else {
                                // Select all
                                setSelectedItems(playlist.songs.map(s => s.song.id));
                            }
                        }}
                        title={selectedItems.length === playlist?.songs.length ? "Deselect all" : "Select all"}
                    >
                        {playlist && selectedItems.length === playlist.songs.length && playlist.songs.length > 0 ? (
                            <CheckSquare size={18} className="text-blue-500" />
                        ) : selectedItems.length > 0 ? (
                            <div className="w-[18px] h-[18px] border-2 border-blue-500 rounded bg-blue-500/30 flex items-center justify-center">
                                <div className="w-2 h-0.5 bg-blue-500" />
                            </div>
                        ) : (
                            <Square size={18} />
                        )}
                    </div>
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

            <CoverPickerModal
                isOpen={isCoverPickerOpen}
                onClose={() => setIsCoverPickerOpen(false)}
                onSelect={(path) => setEditCoverArt(path)}
                currentCover={editCoverArt}
            />

            {/* Share Modal */}
            {showShareModal && (
                <div className="fixed inset-0 bg-black/70 backdrop-blur-sm flex items-center justify-center z-50 p-4">
                    <div className="bg-gray-900 border border-gray-700 rounded-2xl w-full max-w-md p-6 shadow-2xl">
                        <h3 className="text-xl font-bold mb-4 flex items-center gap-2">
                            <Share2 size={20} className="text-emerald-400" />
                            {shareResult ? 'Share Link Ready!' : 'Share Playlist'}
                        </h3>

                        {!shareResult ? (
                            <>
                                <div className="mb-4">
                                    <label className="block text-sm text-gray-400 mb-2">Share Name</label>
                                    <input
                                        type="text"
                                        value={shareName}
                                        onChange={(e) => setShareName(e.target.value)}
                                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-3 text-white focus:outline-none focus:border-emerald-500 transition"
                                        placeholder="Enter a name..."
                                    />
                                </div>

                                {/* Security Settings */}
                                <div className="grid grid-cols-2 gap-4 mb-4">
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2 flex items-center gap-1">
                                            <Clock size={14} /> Validity
                                        </label>
                                        <select
                                            value={shareExpireDays}
                                            onChange={(e) => setShareExpireDays(Number(e.target.value))}
                                            className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-3 text-white focus:outline-none focus:border-emerald-500 transition appearance-none"
                                        >
                                            <option value={1}>1 Day</option>
                                            <option value={7}>7 Days</option>
                                            <option value={30}>30 Days</option>
                                            <option value={-1}>Permanent</option>
                                        </select>
                                    </div>
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2 flex items-center gap-1">
                                            <Lock size={14} /> Password
                                        </label>
                                        <input
                                            type="text"
                                            value={sharePassword}
                                            onChange={(e) => setSharePassword(e.target.value)}
                                            className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-3 text-white focus:outline-none focus:border-emerald-500 transition"
                                            placeholder="(Optional)"
                                        />
                                    </div>
                                </div>
                                <div className="mb-6 p-3 bg-emerald-500/10 border border-emerald-500/30 rounded-lg">
                                    <p className="text-sm text-emerald-400">
                                        {selectedItems.length > 0
                                            ? `Sharing ${selectedItems.length} selected song${selectedItems.length > 1 ? 's' : ''}`
                                            : `Sharing all ${playlist?.songs.length || 0} songs`
                                        }
                                    </p>
                                </div>
                                <div className="flex gap-3">
                                    <button
                                        onClick={() => setShowShareModal(false)}
                                        className="flex-1 px-4 py-3 bg-gray-800 hover:bg-gray-700 text-gray-300 rounded-lg font-medium transition"
                                    >
                                        Cancel
                                    </button>
                                    <button
                                        onClick={handleShare}
                                        disabled={sharing}
                                        className="flex-1 px-4 py-3 bg-emerald-600 hover:bg-emerald-500 text-white rounded-lg font-medium transition disabled:opacity-50"
                                    >
                                        {sharing ? 'Generating...' : 'Generate Link'}
                                    </button>
                                </div>
                            </>
                        ) : (
                            <>
                                <div className="mb-4 bg-black/50 rounded-lg p-3 border border-gray-700">
                                    <p className="text-xs text-gray-400 mb-1">Share Link:</p>
                                    <p className="text-sm text-emerald-400 break-all font-mono">{shareResult.url}</p>
                                </div>
                                <div className="flex gap-3">
                                    <button
                                        onClick={handleCopyShareLink}
                                        className="flex-1 px-4 py-3 bg-blue-600 hover:bg-blue-500 text-white rounded-lg font-medium flex items-center justify-center gap-2 transition"
                                    >
                                        <Copy size={18} />
                                        Copy Link
                                    </button>
                                    <button
                                        onClick={() => window.open(shareResult.url, '_blank')}
                                        className="flex-1 px-4 py-3 bg-gray-700 hover:bg-gray-600 text-white rounded-lg font-medium flex items-center justify-center gap-2 transition"
                                    >
                                        <ExternalLink size={18} />
                                        Open
                                    </button>
                                </div>
                                <button
                                    onClick={() => setShowShareModal(false)}
                                    className="w-full mt-3 text-sm text-gray-500 hover:text-gray-400 transition"
                                >
                                    Close
                                </button>
                            </>
                        )}
                    </div>
                </div>
            )}
        </div>
    );
}


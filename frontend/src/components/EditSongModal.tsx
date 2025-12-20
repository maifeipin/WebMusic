import { useState, useEffect } from 'react';
import { X, Save, Music2, Tag, Image as ImageIcon } from 'lucide-react';
import { updateMedia } from '../services/api';
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

export default function EditSongModal({ isOpen, onClose, song, onSaved }: EditSongModalProps) {
    const [title, setTitle] = useState('');
    const [artist, setArtist] = useState('');
    const [album, setAlbum] = useState('');
    const [genre, setGenre] = useState('');
    const [coverArt, setCoverArt] = useState('');

    const [saving, setSaving] = useState(false);
    const [error, setError] = useState('');
    const [isCoverPickerOpen, setIsCoverPickerOpen] = useState(false);

    useEffect(() => {
        if (song) {
            setTitle(song.title || '');
            setArtist(song.artist || '');
            setAlbum(song.album || '');
            setGenre(song.genre || '');
            setCoverArt(song.coverArt || '');
            setError('');
        }
    }, [song]);

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

    return (
        <>
            <div className="fixed inset-0 z-[80] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
                <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-md shadow-2xl animate-fade-in flex flex-col max-h-[90vh]">
                    {/* Header */}
                    <div className="flex items-center justify-between p-4 border-b border-gray-800 flex-shrink-0">
                        <div className="flex items-center gap-2">
                            <Music2 size={20} className="text-blue-500" />
                            <h2 className="text-lg font-bold">Edit Song Info</h2>
                        </div>
                        <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition">
                            <X size={20} />
                        </button>
                    </div>

                    {/* Body */}
                    <div className="p-4 space-y-4 overflow-y-auto custom-scrollbar">
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
                                    <span className="text-xs text-white">Click Change</span>
                                </div>
                            </div>
                            <div className="flex-1 min-w-0">
                                <label className="block text-xs text-gray-400 mb-1 uppercase tracking-wider font-semibold">Cover Art</label>
                                <div className="text-xs text-gray-300 truncate mb-2" title={coverArt}>
                                    {coverArt || 'No custom cover set'}
                                </div>
                                <button
                                    onClick={() => setIsCoverPickerOpen(true)}
                                    className="px-3 py-1.5 text-xs bg-gray-700 hover:bg-gray-600 text-white rounded-lg transition flex items-center gap-1.5 w-fit"
                                >
                                    <ImageIcon size={14} />
                                    Select from SMB...
                                </button>
                            </div>
                        </div>

                        <div>
                            <label className="block text-sm font-medium text-gray-400 mb-1.5">Title *</label>
                            <input
                                type="text"
                                value={title}
                                onChange={(e) => setTitle(e.target.value)}
                                className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                placeholder="Song title"
                            />
                        </div>

                        <div>
                            <label className="block text-sm font-medium text-gray-400 mb-1.5">Artist</label>
                            <input
                                type="text"
                                value={artist}
                                onChange={(e) => setArtist(e.target.value)}
                                className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                placeholder="Artist name"
                            />
                        </div>

                        <div>
                            <label className="block text-sm font-medium text-gray-400 mb-1.5">Album</label>
                            <input
                                type="text"
                                value={album}
                                onChange={(e) => setAlbum(e.target.value)}
                                className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                placeholder="Album name"
                            />
                        </div>

                        <div>
                            <label className="block text-sm font-medium text-gray-400 mb-1.5">
                                <span className="flex items-center gap-1.5">
                                    <Tag size={14} />
                                    Genre / Tag
                                </span>
                            </label>
                            <input
                                type="text"
                                value={genre}
                                onChange={(e) => setGenre(e.target.value)}
                                className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-2.5 focus:outline-none focus:border-blue-500 transition"
                                placeholder="Genre or tag (e.g., Rock, Jazz, Podcast)"
                            />
                        </div>
                    </div>

                    {/* Footer */}
                    <div className="p-4 border-t border-gray-800 flex justify-end gap-3 flex-shrink-0">
                        <button
                            onClick={onClose}
                            className="px-4 py-2 text-gray-400 hover:text-white hover:bg-white/5 rounded-lg transition"
                        >
                            Cancel
                        </button>
                        <button
                            onClick={handleSave}
                            disabled={saving || !title.trim()}
                            className="px-4 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-lg flex items-center gap-2 transition disabled:opacity-50 disabled:cursor-not-allowed"
                        >
                            <Save size={18} />
                            {saving ? 'Saving...' : 'Save'}
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

import { useState, useEffect } from 'react';
import { X, Save, Music2, Tag } from 'lucide-react';
import { updateMedia } from '../services/api';

interface EditSongModalProps {
    isOpen: boolean;
    onClose: () => void;
    song: {
        id: number;
        title: string;
        artist: string;
        album?: string;
        genre?: string;
    } | null;
    onSaved?: (updatedSong: { id: number; title: string; artist: string; album?: string; genre?: string }) => void;
}

export default function EditSongModal({ isOpen, onClose, song, onSaved }: EditSongModalProps) {
    const [title, setTitle] = useState('');
    const [artist, setArtist] = useState('');
    const [album, setAlbum] = useState('');
    const [genre, setGenre] = useState('');
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState('');

    useEffect(() => {
        if (song) {
            setTitle(song.title || '');
            setArtist(song.artist || '');
            setAlbum(song.album || '');
            setGenre(song.genre || '');
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
                genre: genre.trim()
            });

            onSaved?.({
                id: song.id,
                title: res.data.title,
                artist: res.data.artist,
                album: res.data.album,
                genre: res.data.genre
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
        <div className="fixed inset-0 z-[80] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-md shadow-2xl animate-fade-in">
                {/* Header */}
                <div className="flex items-center justify-between p-4 border-b border-gray-800">
                    <div className="flex items-center gap-2">
                        <Music2 size={20} className="text-blue-500" />
                        <h2 className="text-lg font-bold">Edit Song Info</h2>
                    </div>
                    <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition">
                        <X size={20} />
                    </button>
                </div>

                {/* Body */}
                <div className="p-4 space-y-4">
                    {error && (
                        <div className="p-3 bg-red-500/20 border border-red-500/50 rounded-lg text-red-400 text-sm">
                            {error}
                        </div>
                    )}

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
                <div className="p-4 border-t border-gray-800 flex justify-end gap-3">
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
    );
}

import { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import * as api from '../services/api';

// Song Interface
export interface Song {
    id: number;
    title: string;
    artist: string;
    album: string;
    genre?: string;
    duration: number;
    filePath?: string;
}

interface PlayerContextType {
    currentSong: Song | null;
    isPlaying: boolean;
    playSong: (song: Song) => void;
    togglePlay: () => void;
    // Navigation
    nextSong: () => void;
    prevSong: () => void;
    playNext: () => void;
    playPrev: () => void;
    // Queue
    queue: Song[];
    currentIndex: number;
    addToQueue: (song: Song) => void;
    playQueue: (songs: Song[], startIndex?: number) => void;
    // Transcoding
    showTranscodePrompt: boolean;
    confirmTranscode: (transcode: boolean) => void;
    transcodeMode: boolean;
    // User
    isFavorite: (id: number) => boolean;
    toggleLike: (id: number) => Promise<void>;
    // Song Update
    updateCurrentSong: (updates: Partial<Song>) => void;
}

const PlayerContext = createContext<PlayerContextType | undefined>(undefined);

export const usePlayer = () => {
    const context = useContext(PlayerContext);
    if (!context) throw new Error("usePlayer must be used within PlayerProvider");
    return context;
};

export function PlayerProvider({ children }: { children: ReactNode }) {
    const [currentSong, setCurrentSong] = useState<Song | null>(null);
    const [queue, setQueue] = useState<Song[]>([]);
    const [currentIndex, setCurrentIndex] = useState<number>(-1);
    const [isPlaying, setIsPlaying] = useState(false);
    const [showTranscodePrompt, setShowTranscodePrompt] = useState(false);
    const [pendingSong, setPendingSong] = useState<Song | null>(null);
    const [transcodeMode, setTranscodeMode] = useState(false);
    const [favorites, setFavorites] = useState<Set<number>>(new Set());

    // Load Favorites
    useEffect(() => {
        const loadFavorites = async () => {
            try {
                const res = await api.getFavoriteIds();
                setFavorites(new Set(res.data));
            } catch (e) {
                console.error("Failed to load favorites", e);
            }
        };
        loadFavorites();
    }, []);

    // Record History on Song Change
    useEffect(() => {
        if (currentSong) {
            // Debounce? Or just log.
            // Ideally should check if played > 5s? For now log start.
            api.addToHistory(currentSong.id).catch(err => console.error("History log failed", err));
        }
    }, [currentSong]);

    const isFavorite = (id: number) => favorites.has(id);

    const toggleLike = async (id: number) => {
        try {
            const wasFav = favorites.has(id);
            // Optimistic update
            const nextFavs = new Set(favorites);
            if (wasFav) nextFavs.delete(id);
            else nextFavs.add(id);
            setFavorites(nextFavs);

            await api.toggleFavorite(id);
        } catch (e) {
            console.error("Failed to toggle favorite", e);
            // Revert? simpler to just reload or ignore for MVP
        }
    };

    const playSong = (song: Song) => {
        playQueue([song], 0);
    };

    const playQueue = (songs: Song[], startIndex: number = 0) => {
        setQueue(songs);
        setCurrentIndex(startIndex);
        loadSong(songs[startIndex]);
    };

    const addToQueue = (song: Song) => {
        setQueue(prev => [...prev, song]);
    };

    const loadSong = (song: Song) => {
        // 1. Check Format
        const ext = song.filePath ? song.filePath.split('.').pop()?.toLowerCase() : '';
        const supported = ['mp3', 'wav', 'ogg', 'm4a', 'flac', 'aac'];
        // Known problematic: wma, ape, dsf
        const webSafe = supported.includes(ext || '');

        if (webSafe) {
            setTranscodeMode(false);
            setCurrentSong(song);
            setIsPlaying(true);
        } else {
            setPendingSong(song);
            setShowTranscodePrompt(true);
        }
    };

    const confirmTranscode = (shouldTranscode: boolean) => {
        setShowTranscodePrompt(false);
        if (shouldTranscode && pendingSong) {
            setTranscodeMode(true);
            setCurrentSong(pendingSong);
            setIsPlaying(true);
        } else {
            setPendingSong(null);
            setIsPlaying(false);
        }
        setPendingSong(null);
    };

    const updateCurrentSong = (updates: Partial<Song>) => {
        if (currentSong) {
            const updated = { ...currentSong, ...updates };
            setCurrentSong(updated);
            // Also update in queue if present
            setQueue(prev => prev.map(s => s.id === updated.id ? updated : s));
        }
    };

    const togglePlay = () => setIsPlaying(!isPlaying);

    const nextSong = () => {
        if (currentIndex < queue.length - 1) {
            const nextIdx = currentIndex + 1;
            setCurrentIndex(nextIdx);
            loadSong(queue[nextIdx]);
        }
    };

    const prevSong = () => {
        if (currentIndex > 0) {
            const prevIdx = currentIndex - 1;
            setCurrentIndex(prevIdx);
            loadSong(queue[prevIdx]);
        }
    };

    return (
        <PlayerContext.Provider value={{
            currentSong,
            isPlaying,
            playSong,
            togglePlay,
            nextSong,
            prevSong,
            playNext: nextSong, // Alias
            playPrev: prevSong, // Alias
            showTranscodePrompt,
            confirmTranscode,
            transcodeMode,
            playQueue,
            queue,
            currentIndex,
            addToQueue,
            isFavorite,
            toggleLike,
            updateCurrentSong
        }}>
            {children}
        </PlayerContext.Provider>
    );
};

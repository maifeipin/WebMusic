import { useState } from 'react';
import { X, Upload, FileText } from 'lucide-react';
import { importFavorites } from '../services/api';

interface ImportFavoritesModalProps {
    onClose: () => void;
}

export default function ImportFavoritesModal({ onClose }: ImportFavoritesModalProps) {
    const [file, setFile] = useState<File | null>(null);
    const [textInput, setTextInput] = useState('');
    const [status, setStatus] = useState<string | null>(null);
    const [loading, setLoading] = useState(false);

    const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files && e.target.files[0]) {
            setFile(e.target.files[0]);
            setTextInput(''); // Clear text if file selected
        }
    };

    const handleImport = async () => {
        setLoading(true);
        setStatus(null);

        try {
            let uploadFile = file;

            if (!uploadFile && textInput.trim()) {
                // Create file from text input
                const blob = new Blob([textInput], { type: 'text/plain' });
                uploadFile = new File([blob], "favorites_import.txt", { type: "text/plain" });
            }

            if (!uploadFile) {
                setStatus('Please select a file or paste text.');
                setLoading(false);
                return;
            }

            const formData = new FormData();
            formData.append('file', uploadFile);

            const res = await importFavorites(formData);
            setStatus(`Successfully imported ${res.data.imported} songs!`);

            // Clear inputs on success
            setFile(null);
            setTextInput('');
            setTimeout(() => {
                onClose();
            }, 2000); // Close after 2s
        } catch (error) {
            console.error(error);
            setStatus('Import failed. Please check the file format.');
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-md flex flex-col shadow-2xl animate-fade-in">
                <div className="flex items-center justify-between p-4 border-b border-gray-800">
                    <h3 className="font-bold flex items-center gap-2">
                        <Upload size={18} className="text-blue-500" />
                        Import Favorites
                    </h3>
                    <button onClick={onClose}><X size={20} className="text-gray-400 hover:text-white" /></button>
                </div>

                <div className="p-6 space-y-6">
                    {/* File Upload Area */}
                    <div className="border-2 border-dashed border-gray-700 hover:border-blue-500 rounded-xl p-6 text-center transition bg-black/20">
                        <input
                            type="file"
                            accept=".txt"
                            onChange={handleFileChange}
                            className="hidden"
                            id="file-upload"
                        />
                        <label htmlFor="file-upload" className="cursor-pointer block">
                            <FileText size={32} className="mx-auto text-gray-500 mb-2" />
                            <p className="text-sm text-gray-400">
                                {file ? (
                                    <span className="text-blue-400 font-medium">{file.name}</span>
                                ) : (
                                    "Click to upload .txt file"
                                )}
                            </p>
                        </label>
                    </div>

                    <div className="relative">
                        <div className="absolute inset-0 flex items-center">
                            <div className="w-full border-t border-gray-800"></div>
                        </div>
                        <div className="relative flex justify-center text-xs uppercase">
                            <span className="bg-gray-900 px-2 text-gray-500">Or paste content</span>
                        </div>
                    </div>

                    {/* Text Area */}
                    <textarea
                        value={textInput}
                        onChange={(e) => {
                            setTextInput(e.target.value);
                            setFile(null);
                        }}
                        placeholder="Paste file paths here (one per line)..."
                        className="w-full h-32 bg-black/30 border border-gray-700 rounded-xl p-3 text-sm text-gray-300 focus:outline-none focus:border-blue-500 resize-none font-mono"
                    />

                    {/* Status Message */}
                    {status && (
                        <div className={`text-sm text-center ${status.includes('Success') ? 'text-green-400' : 'text-red-400'}`}>
                            {status}
                        </div>
                    )}

                    {/* Action Button */}
                    <button
                        onClick={handleImport}
                        disabled={loading || (!file && !textInput.trim())}
                        className="w-full py-3 bg-blue-600 hover:bg-blue-500 disabled:bg-gray-800 disabled:text-gray-500 text-white rounded-xl font-medium transition shadow-lg shadow-blue-500/20 flex items-center justify-center gap-2"
                    >
                        {loading ? 'Importing...' : 'Start Import'}
                    </button>

                    <p className="text-xs text-gray-600 text-center">
                        Format: One full file path per line. <br />
                        Example: /volume1/music/Jay Chou/Fantasy/01.mp3
                    </p>
                </div>
            </div>
        </div>
    );
}

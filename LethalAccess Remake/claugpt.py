import os

# List of directories to exclude
EXCLUDED_DIRS = {'bin', 'obj', 'config'}

# File extensions to include
INCLUDED_EXTENSIONS = {'.cs', '.json'}

def is_valid_file(file_name):
    return any(file_name.endswith(ext) for ext in INCLUDED_EXTENSIONS)

def should_exclude_directory(dir_name):
    return dir_name in EXCLUDED_DIRS

def main():
    output_file = 'Lethal Access.txt'
    with open(output_file, 'w', encoding='utf-8') as output:
        for root, dirs, files in os.walk('.'):
            # Modify dirs in-place to exclude certain directories
            dirs[:] = [d for d in dirs if not should_exclude_directory(d)]
            
            for file in files:
                if is_valid_file(file):
                    file_path = os.path.join(root, file)
                    try:
                        with open(file_path, 'r', encoding='utf-8') as f:
                            content = f.read()
                        output.write(f'{file}\n')
                        output.write(f'{file_path}\n\n')
                        output.write(f'{content}\n\n\n\n\n\n\n')
                        print(f'Processed file: {file_path}')
                    except Exception as e:
                        print(f'Error reading file {file_path}: {e}')
    
    print(f'All files processed. Output written to {output_file}.')

if __name__ == "__main__":
    main()

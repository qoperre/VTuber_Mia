from dotenv import load_dotenv
from llama_cpp import Llama

# .env 파일 로드 (Hugging Face 토큰이 환경 변수에 있다면 유지)
load_dotenv()

# GGUF 로컬 모델 로드
llm = Llama.from_pretrained(
    repo_id="tensorblock/Llama-3.1-Korean-8B-Instruct-GGUF",
    filename="Llama-3.1-Korean-8B-Instruct-Q2_K.gguf",
    # verbose=False # 모델 로딩/추론 시 나오는 긴 로그를 끄고 싶다면 주석을 해제하세요.
)

def get_completion(prompt):
    # 1. 생성된 결과를 'response'라는 변수에 저장합니다.
    response = llm.create_chat_completion(
        messages=[
            {"role": "user", "content": prompt}
        ]
    )
    
    # 2. llama_cpp의 올바른 출력 구조에 맞게 텍스트만 추출합니다.
    return response['choices'][0]['message']['content']

while True:
    user_input = input("User: ")
    
    # 종료 조건 추가 (선택 사항)
    if user_input.lower() in ['exit', 'quit']:
        print("대화를 종료합니다.")
        break
        
    print("AI: " + get_completion(user_input))